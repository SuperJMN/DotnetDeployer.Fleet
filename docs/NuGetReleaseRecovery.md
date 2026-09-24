# Recovering a multi-package NuGet release

Fleet persists a release manifest before its first NuGet push. The manifest is keyed by
project ID and the full validated Git commit SHA. It records the package version, feed,
exact IDs, SHA-256 of each staged `.nupkg`, NuGet's signature-independent content hash,
and each durable artifact path. It also pins the push secret name and whether GitHub
publication follows NuGet. Each package separately records `Prepared`, `Publishing`,
`AwaitingIndex`, `Incomplete`, `Complete`, or `InterventionRequired`.

## Deployment prerequisite

Deploy the coordinator before workers. Set `Releases:RootDir` to a persistent, backed-up
volume shared by coordinator replacements. If unset, the store is
`<Artifacts:RootDir>/nuget-releases`. Preserve this directory across coordinator
restarts, worker upgrades, and finished-job cleanup. Back up its `manifest.json`,
`packages/*.nupkg`, and `progress/*.json` together. Do not run mixed release jobs on an
old coordinator or old worker. The worker needs NuGet v3 `PackageBaseAddress/3.0.0`
lookup for HTTP feeds; folder feeds are supported for staging.

## Inspect and retry

An administrator can GET `/api/jobs/{jobId}/nuget-release` to see the manifest and
per-package progress. Read the job logs and the exact package versions on the feed.
The coordinator also stores the same data under
`<Releases:RootDir>/<project-id-N>/<commit-sha>/`.

For `AwaitingIndex`, Fleet releases the worker and queues another attempt after four
minutes. The coordinator persists this decision, so a restart does not lose it. Each
attempt verifies the exact remote package before any new push and reuses the stored
bytes. After twelve failed attempts, Fleet changes the state to `Incomplete` and
reports that manual recovery is required.

For `Publishing`, `AwaitingIndex`, or `Incomplete`, an administrator can also POST
`/api/projects/{projectId}/deploy` with
`{"commitSha":"<full manifest SHA>"}` to queue another job for the **same project and
full commit SHA**. The worker fetches the existing manifest and package bytes from the
coordinator, checks all remote ID/version pairs, and resumes the missing packages. NuGet
recovery does not require Git, build, tests, or repacking. A push timeout, lost response,
or worker crash is
therefore safe to retry: a matching downloadable package becomes `Complete`; a missing
one is pushed from the same stored artifact. Delayed availability is polled briefly
after each push; the scheduled retry waits for NuGet indexing without occupying a
worker.

If `InterventionRequired` appears, stop automated retries. Preserve the manifest,
staged files, logs, and feed response. Compare the feed's exact ID/version package to
the manifest and identify the owner of the conflicting package. NuGet package versions
on nuget.org cannot be overwritten reliably. Publish a corrected release under a new
version or use the feed owner's approved removal process; never edit the manifest,
replace its staged bytes, or treat HTTP 409 as equivalence. If coordinator storage is
lost, do not retry publication until the original manifest and artifacts are restored
from backup or an operator has established their exact provenance.

When the manifest requires GitHub publication, the worker fetches the validated source
after NuGet recovery, then runs that stage. If Git is unavailable, the NuGet packages
remain verified and a later retry can finish GitHub publication. The GitHub stage starts
only after every manifest package is verified downloadable. NuGet publication cannot be
made atomically visible on nuget.org: during
the sequence, consumers may see a subset. The release remains incomplete until all
packages are verified.

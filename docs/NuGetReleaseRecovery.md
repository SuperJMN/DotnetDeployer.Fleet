# Recovering a multi-package NuGet release

Fleet persists a release manifest before its first NuGet push. The manifest is keyed by
project ID and the full validated Git commit SHA. It records the package version, feed,
exact IDs, SHA-256 of each staged `.nupkg`, NuGet's signature-independent content hash,
and each durable artifact path. It also pins the push secret name and whether GitHub
publication follows NuGet. Each package separately records `Prepared`, `Publishing`,
`AwaitingIndex`, `Incomplete`, `Complete`, or `InterventionRequired`.
`Complete` means NuGet accepted this push or the exact existing package was verified.

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

For `AwaitingIndex`, a push returned a duplicate or ambiguous result and the exact
package cannot yet be downloaded to establish whether that push succeeded. The
deployment stays pending and Fleet releases the worker.
The coordinator requeues the same job after four minutes, so a restart does not lose
the wait. Each attempt verifies unresolved remote packages before a new push and
reuses the stored bytes. After 24 hours from manifest preparation, Fleet makes one
final feed check; if the package is still unavailable, it marks the deployment failed
and requires manual recovery.
Older failed jobs created before this change still use the legacy retry path.

For `Publishing`, `AwaitingIndex`, or `Incomplete`, an administrator can also POST
`/api/projects/{projectId}/deploy` with
`{"commitSha":"<full manifest SHA>"}` to queue another job for the **same project and
full commit SHA**. The worker fetches the existing manifest and package bytes from the
coordinator, checks unresolved remote ID/version pairs, and resumes the missing packages. NuGet
recovery does not require Git, build, tests, or repacking. A push timeout, lost response,
or worker crash is
therefore safe to retry: a matching downloadable package becomes `Complete`; a missing
one is pushed from the same stored artifact. A successful push response completes
that package immediately. A duplicate or ambiguous response is checked against the
feed; if it is not yet downloadable, the scheduled retry waits for indexing without
occupying a worker.

If `InterventionRequired` appears, stop automated retries. Preserve the manifest,
staged files, logs, and feed response. Compare the feed's exact ID/version package to
the manifest and identify the owner of the conflicting package. NuGet package versions
on nuget.org cannot be overwritten reliably. Publish a corrected release under a new
version or use the feed owner's approved removal process; never edit the manifest,
replace its staged bytes, or treat HTTP 409 as equivalence. If coordinator storage is
lost, do not retry publication until the original manifest and artifacts are restored
from backup or an operator has established their exact provenance.

When the manifest requires GitHub publication, the worker fetches the validated source
after NuGet recovery, then runs that stage. If Git is unavailable, the accepted NuGet
pushes remain recorded and a later retry can finish GitHub publication. The GitHub stage
starts once every manifest package has either received a successful push response or
has been verified as the exact existing package. NuGet publication cannot be
made atomically visible on nuget.org: during
the sequence, consumers may see a subset. NuGet may still be validating or indexing
an accepted package after Fleet completes the deployment. NuGet can also report a
later validation failure. Fleet's deployment status records push acceptance; it does
not claim that consumers can already restore the package.

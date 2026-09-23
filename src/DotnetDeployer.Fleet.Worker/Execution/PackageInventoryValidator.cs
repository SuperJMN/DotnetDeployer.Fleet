namespace DotnetDeployer.Fleet.WorkerService.Execution;

public sealed record PackageInventoryValidationResult(
    bool IsValid,
    IReadOnlyList<string> ExpectedIds,
    IReadOnlyList<string> ProducedIds,
    IReadOnlyList<string> MissingIds,
    IReadOnlyList<string> ExtraIds,
    IReadOnlyList<string> DuplicateIds,
    string? ErrorMessage);

public static class PackageInventoryValidator
{
    public static PackageInventoryValidationResult Validate(
        IReadOnlyCollection<string>? expectedIds,
        IReadOnlyCollection<string>? producedIds)
    {
        var expected = (expectedIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var produced = (producedIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();

        if (expected.Count == 0)
        {
            return new PackageInventoryValidationResult(
                IsValid: false,
                ExpectedIds: expected,
                ProducedIds: produced.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                MissingIds: [],
                ExtraIds: produced.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
                DuplicateIds: [],
                ErrorMessage: "No expected package inventory policy configured. A package release job requires declared package IDs.");
        }

        // Check duplicates among produced packages
        var duplicates = produced
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var distinctProduced = produced
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = expected
            .Where(e => !distinctProduced.Contains(e, StringComparer.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var extra = distinctProduced
            .Where(p => !expected.Contains(p, StringComparer.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (duplicates.Count > 0 || missing.Count > 0 || extra.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add($"missing: [{string.Join(", ", missing)}]");
            if (extra.Count > 0)
                parts.Add($"extra: [{string.Join(", ", extra)}]");
            if (duplicates.Count > 0)
                parts.Add($"duplicate: [{string.Join(", ", duplicates)}]");

            var error = $"Package inventory mismatch ({string.Join("; ", parts)}). Expected [{string.Join(", ", expected)}], but produced [{string.Join(", ", distinctProduced)}].";

            return new PackageInventoryValidationResult(
                IsValid: false,
                ExpectedIds: expected,
                ProducedIds: distinctProduced,
                MissingIds: missing,
                ExtraIds: extra,
                DuplicateIds: duplicates,
                ErrorMessage: error);
        }

        return new PackageInventoryValidationResult(
            IsValid: true,
            ExpectedIds: expected,
            ProducedIds: distinctProduced,
            MissingIds: [],
            ExtraIds: [],
            DuplicateIds: [],
            ErrorMessage: null);
    }
}

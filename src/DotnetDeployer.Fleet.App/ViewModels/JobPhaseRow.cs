using System.Collections.ObjectModel;
using System.Globalization;
using DotnetDeployer.Fleet.Core.Domain;
using ReactiveUI;
using Zafiro.UI;

namespace DotnetDeployer.Fleet.App.ViewModels;

/// <summary>
/// View-model row for a single <see cref="JobPhase"/>. Wraps the domain entity
/// with display-friendly properties (icon by status, formatted duration,
/// localised display name) so the AXAML stays declarative. <see cref="Children"/>
/// holds inner phases nested via interval containment, consumed by the TreeView's
/// HierarchicalDataTemplate.
/// </summary>
public sealed class JobPhaseRow : ReactiveObject
{
    private readonly Func<string, string> formatName;
    private string icon = "";
    private string displayName = "";
    private string durationText = "";

    internal JobPhaseRow(
        JobPhaseModel model,
        ReadOnlyObservableCollection<JobPhaseRowContainer> children,
        Func<string, string> formatName)
    {
        this.formatName = formatName;
        Children = children;
        Update(model);
    }

    public Guid Id { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset EndedSort { get; private set; }
    public string Icon
    {
        get => icon;
        private set => this.RaiseAndSetIfChanged(ref icon, value);
    }

    public string DisplayName
    {
        get => displayName;
        private set => this.RaiseAndSetIfChanged(ref displayName, value);
    }

    public string DurationText
    {
        get => durationText;
        private set => this.RaiseAndSetIfChanged(ref durationText, value);
    }

    public ReadOnlyObservableCollection<JobPhaseRowContainer> Children { get; }

    internal void Update(JobPhaseModel model)
    {
        var phase = model.Phase;
        Id = phase.Id;
        StartedAt = phase.StartedAt;
        EndedSort = phase.EndedAt ?? DateTimeOffset.MaxValue;
        Icon = phase.EndedAt is null
            ? "⏳"
            : phase.Status switch
            {
                PhaseStatus.Ok => "✅",
                PhaseStatus.Fail => "❌",
                PhaseStatus.Waiting => "⏳",
                _ => "•"
            };

        DurationText = phase.DurationMs switch
        {
            { } ms when ms < 1000 => $"{ms} ms",
            { } ms => $"{(ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}s",
            null when phase.EndedAt is null => "running…",
            _ => ""
        };

        DisplayName = formatName(phase.Name);
    }
}

public sealed class JobPhaseRowContainer : IdentityContainer<JobPhaseRow>;

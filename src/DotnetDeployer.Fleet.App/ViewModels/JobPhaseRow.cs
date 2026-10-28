using System.Collections.ObjectModel;
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
    private IIcon icon = new Icon();
    private Tone tone;
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
    public IIcon Icon
    {
        get => icon;
        private set => this.RaiseAndSetIfChanged(ref icon, value);
    }

    public Tone Tone
    {
        get => tone;
        private set => this.RaiseAndSetIfChanged(ref tone, value);
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
        var (iconCode, phaseTone) = phase.EndedAt is null
            ? ("mdi-progress-clock", Tone.Info)
            : phase.Status switch
            {
                PhaseStatus.Ok => ("mdi-check-circle", Tone.Success),
                PhaseStatus.Fail => ("mdi-close-circle", Tone.Danger),
                PhaseStatus.Waiting => ("mdi-progress-clock", Tone.Info),
                _ => ("mdi-circle-small", Tone.Neutral)
            };
        if (Icon.Source != iconCode)
            Icon = new Icon(iconCode);
        Tone = phaseTone;

        // Same format as the job's elapsed time so phases and totals read alike.
        DurationText = phase.DurationMs switch
        {
            { } ms => JobDurationFormatter.Format(ms),
            null when phase.EndedAt is null => "running…",
            _ => ""
        };

        DisplayName = formatName(phase.Name);
    }
}

public sealed class JobPhaseRowContainer : IdentityContainer<JobPhaseRow>;

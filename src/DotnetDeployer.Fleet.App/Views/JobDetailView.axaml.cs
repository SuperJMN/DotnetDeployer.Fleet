using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaTerminal;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.App.Views;

public partial class JobDetailView : UserControl
{
    private readonly DispatcherTimer redrawTimer;
    private JobDetailViewModel? _attachedVm;
    private TerminalControlModel? _watchedModel;
    private int _writtenColumns;

    public JobDetailView()
    {
        InitializeComponent();
        redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        redrawTimer.Tick += (_, _) =>
        {
            redrawTimer.Stop();
            _attachedVm?.RedrawTerminal();
        };
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachFromVm();

        if (DataContext is not JobDetailViewModel vm) return;

        _attachedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        // The terminal control shows vm.TerminalModel through a binding.
        vm.SetTerminalModel(new TerminalControlModel());
        WatchTerminalSize(vm.TerminalModel);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobDetailViewModel.TerminalModel))
            WatchTerminalSize(_attachedVm?.TerminalModel);
    }

    /// <summary>
    /// The terminal does not reflow text that was written at another width, so the log is
    /// redrawn whenever the number of columns changes (for example when the hidden log is
    /// first shown and gets its real width).
    /// </summary>
    private void WatchTerminalSize(TerminalControlModel? model)
    {
        if (ReferenceEquals(model, _watchedModel)) return;

        if (_watchedModel is not null)
            _watchedModel.SizeChanged -= OnTerminalSizeChanged;

        _watchedModel = model;
        if (model is null) return;

        _writtenColumns = model.Terminal.Cols;
        model.SizeChanged += OnTerminalSizeChanged;
    }

    private void OnTerminalSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        if (e.Cols == _writtenColumns) return;

        redrawTimer.Stop();
        redrawTimer.Start();
    }

    private void DetachFromVm()
    {
        redrawTimer.Stop();
        WatchTerminalSize(null);

        if (_attachedVm is not null)
        {
            _attachedVm.PropertyChanged -= OnVmPropertyChanged;
            _attachedVm.DetachTerminalModel();
        }

        _attachedVm = null;
    }
}

using Avalonia.Controls;
using AvaloniaTerminal;
using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.App.Views;

public partial class JobDetailView : UserControl
{
    private JobDetailViewModel? _attachedVm;

    public JobDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _attachedVm?.DetachTerminalModel();
        _attachedVm = null;

        if (DataContext is not JobDetailViewModel vm) return;

        var terminal = LogTerminal;
        if (terminal is null) return;

        terminal.Model ??= new TerminalControlModel();
        vm.SetTerminalModel(terminal.Model);
        _attachedVm = vm;
    }
}

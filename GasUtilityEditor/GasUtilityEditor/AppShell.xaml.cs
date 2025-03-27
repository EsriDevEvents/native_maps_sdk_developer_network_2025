using CommunityToolkit.Mvvm.Messaging;

namespace GasUtilityEditor;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
    }

    private bool showBranchVersion = false;

    private void OnTitleClicked(object sender, EventArgs e)
    {
        showBranchVersion = !showBranchVersion;
        WeakReferenceMessenger.Default.Send(new DisplayBranchMessage(showBranchVersion));
    }
}

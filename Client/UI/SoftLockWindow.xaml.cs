using System.Windows;

namespace ClassroomController.Client.UI;

public partial class SoftLockWindow : Window
{
    public SoftLockWindow()
    {
        InitializeComponent();
    }

    public void ForceClose()
    {
        Close();
    }
}

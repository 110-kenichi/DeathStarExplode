using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OscTest.ViewModels;

namespace OscTest.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        this.Unloaded += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
        };
    }
}

using Microsoft.UI.Xaml.Controls;
using Tasklabe.App.ViewModels;

namespace Tasklabe.App.Views;

public sealed partial class SignInView : UserControl
{
    public SignInView(SignInViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SignInViewModel ViewModel { get; }
}

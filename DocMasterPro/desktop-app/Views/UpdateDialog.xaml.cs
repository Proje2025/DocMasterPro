using System;
using System.Windows;
using System.Windows.Input;
using DocConverter.ViewModels;

namespace DocConverter.Views
{
    public partial class UpdateDialog : Window
    {
        public UpdateViewModel ViewModel { get; }

        public UpdateDialog(UpdateViewModel? viewModel = null)
        {
            InitializeComponent();
            ViewModel = viewModel ?? new UpdateViewModel();
            DataContext = ViewModel;
            ViewModel.RequestClose = () => Close();

            Loaded += async (_, _) =>
            {
                if (ViewModel.State == Models.UpdateState.Checking)
                {
                    await ViewModel.CheckUpdatesCommand.ExecuteAsync(null);
                }
            };
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}

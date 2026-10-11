using System.ComponentModel;
using System.Windows;
using DocConverter.ViewModels;

namespace DocConverter.Views
{
    /// <summary>
    /// Microsoft Office yokken LibreOffice'in indirilmesi için kullanıcıdan onay alan ve ilerlemeyi gösteren pencere.
    /// DialogResult true ise dönüştürme motoru kullanıma hazırdır.
    /// </summary>
    public partial class OfficeEngineSetupDialog : Window
    {
        private readonly OfficeEngineSetupViewModel _viewModel;
        private bool _closingFromViewModel;

        public OfficeEngineSetupDialog(OfficeEngineSetupViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;
            viewModel.CloseRequested += OnCloseRequested;
        }

        private void OnCloseRequested(object? sender, bool ready)
        {
            _closingFromViewModel = true;

            // ShowDialog ile açıldıysa sonucu bildir; Show ile açıldıysa yalnızca kapat.
            try
            {
                DialogResult = ready;
            }
            catch (System.InvalidOperationException)
            {
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Pencere çarpıdan ya da Alt+F4 ile kapatılırsa süren indirme arka planda devam etmesin.
            if (!_closingFromViewModel && _viewModel.IsWorking)
                _viewModel.CancelCommand.Execute(null);

            base.OnClosing(e);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            base.OnClosed(e);
        }
    }
}

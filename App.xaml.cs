using SellerOps.App.Data;
using System;
using System.Text;
using System.Windows;

namespace SellerOps.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            try
            {
                // ВАЖНО: схема/колонки должны быть обновлены ДО открытия UI
                AppDbContext.Instance.EnsureUpToDate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Ошибка инициализации БД",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                Shutdown();
                return;
            }

            var main = new MainWindow();
            main.Show();
        }
    }
}

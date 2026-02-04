using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using SellerOps.App.Services;

namespace SellerOps.App.Views
{
    public partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
        }

        // ====== навигация ======
        private void Supplies_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new SuppliesPage());

        private void Products_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new ProductsPage());

        private void Analytics_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new AnalyticsPage());
        private void Marketplace_Click(object sender, RoutedEventArgs e)
    => NavigationService?.Navigate(new MarketplacePage());

        private void PricesAndDiscounts_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new PricesAndDiscountsPage());

        private void Common_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new CommonPage());

        private void WbSettings_Click(object sender, RoutedEventArgs e)
            => NavigationService?.Navigate(new WbSettingsPage());

        // ====== выбор темы (через sender, без x:Name) ======
        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cmb &&
                cmb.SelectedItem is ComboBoxItem item &&
                item.Tag is string path &&
                !string.IsNullOrWhiteSpace(path))
            {
                ThemeManager.ApplyTheme(path);
            }
        }
    }
}

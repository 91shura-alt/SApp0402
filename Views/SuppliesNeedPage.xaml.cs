using System.Windows;
using System.Windows.Controls;

namespace SellerOps.App.Views
{
    public partial class SuppliesNeedPage : Page
    {
        public SuppliesNeedPage()
        {
            InitializeComponent();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.GoBack();
        }
    }
}

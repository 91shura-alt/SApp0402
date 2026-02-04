using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SellerOps.App.Domain;
using SellerOps.App.Services;

namespace SellerOps.App.Views
{
    public partial class SuppliesPlanPage : Page
    {
        private readonly SuppliesService _svc;
        private readonly Supply _supply;

        public SuppliesPlanPage(Supply supply)
        {
            InitializeComponent();
            _svc = new SuppliesService();
            _supply = supply;

            Loaded += Page_Loaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadTotalsByBox();
        }

        private async Task LoadTotalsByBox()
        {
            TotalsGrid.ItemsSource = await _svc.GetBoxTotalsAsync(_supply);
        }
    }
}

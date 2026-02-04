using System;
using System.Linq;
using System.Windows;

namespace SellerOps.App.Services
{
    public static class ThemeManager
    {
        private const string ThemeTag = "APP_THEME_DICTIONARY";

        public static void ApplyTheme(string themeFileRelativePath) // e.g. "Themes/CupertinoQuartz.xaml"
        {
            if (Application.Current == null) return;

            var appRes = Application.Current.Resources;
            // удалить старую тему (если была)
            var existing = appRes.MergedDictionaries
                                 .FirstOrDefault(d => Equals(d[ThemeTag], true));
            if (existing != null)
                appRes.MergedDictionaries.Remove(existing);

            // загрузить новую
            var dict = new ResourceDictionary
            {
                Source = new Uri(themeFileRelativePath, UriKind.Relative)
            };
            // помечаем словарь, чтобы можно было заменить в будущем
            dict[ThemeTag] = true;

            appRes.MergedDictionaries.Add(dict);
        }
    }
}

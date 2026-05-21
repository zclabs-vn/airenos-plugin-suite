using System.Windows;
using System.Windows.Controls;

namespace AirenoOS.Revit.Plugin.UI
{
    /// <summary>
    /// Two-field WPF prompt for endpoint URL + bearer token, built in code (no XAML).
    /// Kept code-only to avoid the BAML/MSBuild glue that pure-XAML windows pull in;
    /// a Revit plugin's UI surface here is too small to justify that cost.
    /// </summary>
    internal class ConnectDialog : Window
    {
        private readonly TextBox _endpointBox;
        private readonly PasswordBox _tokenBox;

        public string Endpoint
        {
            get => _endpointBox.Text;
            set => _endpointBox.Text = value;
        }

        public string Token
        {
            get => _tokenBox.Password;
            set => _tokenBox.Password = value;
        }

        public ConnectDialog()
        {
            Title = "AirenoOS — Connect";
            Width = 460;
            Height = 220;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 4; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Insert(2, new RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var endpointLabel = new TextBlock { Text = "Endpoint URL", VerticalAlignment = VerticalAlignment.Center };
            _endpointBox = new TextBox { Margin = new Thickness(0, 0, 0, 0) };
            Grid.SetRow(endpointLabel, 0); Grid.SetColumn(endpointLabel, 0);
            Grid.SetRow(_endpointBox, 0);  Grid.SetColumn(_endpointBox, 1);

            var tokenLabel = new TextBlock { Text = "Bearer token", VerticalAlignment = VerticalAlignment.Center };
            _tokenBox = new PasswordBox();
            Grid.SetRow(tokenLabel, 1); Grid.SetColumn(tokenLabel, 0);
            Grid.SetRow(_tokenBox, 1);  Grid.SetColumn(_tokenBox, 1);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "Connect", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90 };
            ok.Click += (_, _) => { DialogResult = true; Close(); };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 4); Grid.SetColumn(buttons, 0); Grid.SetColumnSpan(buttons, 2);

            grid.Children.Add(endpointLabel);
            grid.Children.Add(_endpointBox);
            grid.Children.Add(tokenLabel);
            grid.Children.Add(_tokenBox);
            grid.Children.Add(buttons);

            Content = grid;
        }
    }
}

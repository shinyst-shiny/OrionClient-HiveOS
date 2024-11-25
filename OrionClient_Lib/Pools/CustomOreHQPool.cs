using Solnet.Wallet;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public class CustomOreHQPool : OreHQPool
    {
        public override string Name { get; } = "Custom Ore-HQ Pool";
        public override string DisplayName => $"{Name} ({WebsocketUrl?.Host ?? "Unknown"})";
        public override string Description => $"Custom pool using Ore-HQ pool implementation";
        public override bool Display => true;
        public override Coin Coins { get; } = Coin.Ore;

        public override Dictionary<string, string> Features { get; } = new Dictionary<string, string>();

        public override bool HideOnPoolList { get; } = false;
        public override string HostName { get; protected set; }

        public override Uri WebsocketUrl => _poolSettings?.CustomDomain == null ? null : new Uri($"wss://{_poolSettings.CustomDomain}/v2/ws?timestamp={_timestamp}");

        public override void SetWalletInfo(Wallet wallet, string publicKey)
        {
            _poolSettings ??= new HQPoolSettings(Name);

            if (String.IsNullOrEmpty(HostName))
            {
                _poolSettings.LoadAsync().Wait();

                HostName = _poolSettings.CustomDomain;
            }

            base.SetWalletInfo(wallet, publicKey);
        }

        public override async Task<(bool, string)> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            try
            {
                if (initialSetup || !Uri.TryCreate(_poolSettings.CustomDomain, UriKind.RelativeOrAbsolute, out var _))
                {
                    TextPrompt<string> textPrompt = new TextPrompt<string>("Enter url for custom pool: ");
                    textPrompt.AllowEmpty();
                    
                    if(!String.IsNullOrEmpty(_poolSettings.CustomDomain))
                    {
                        textPrompt.DefaultValue(_poolSettings.CustomDomain);
                    }

                    textPrompt.Validate((str) =>
                    {
                        if(String.IsNullOrEmpty(str))
                        {
                            return true;
                        }

                        if(Uri.TryCreate(str, UriKind.Absolute, out Uri _))
                        {
                            return true;
                        }

                        str = $"http://{str}";


                        if (Uri.TryCreate(str, UriKind.Absolute, out Uri _))
                        {
                            return true;
                        }

                        return false;
                    });

                    string response = await textPrompt.ShowAsync(AnsiConsole.Console, token);

                    if (String.IsNullOrEmpty(response))
                    {
                        return (false, String.Empty);
                    }

                    if (!Uri.TryCreate(response, UriKind.Absolute, out var customDomain))
                    {
                        response = $"http://{response}";

                        if(!Uri.TryCreate(response, UriKind.Absolute, out customDomain))
                        {
                            return (false, $"Invalid url");
                        }
                    }

                    _poolSettings.CustomDomain = customDomain.Host;
                    await _poolSettings.SaveAsync();

                    //Initialize client
                    _client = new HttpClient
                    {
                        BaseAddress = new Uri($"https://{WebsocketUrl.Host}")
                    };
                }
            }
            finally
            {
                AnsiConsole.Clear();
            }

            return await base.SetupAsync(token, initialSetup);
        }
    }
}

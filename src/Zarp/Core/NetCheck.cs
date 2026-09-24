using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Zarp.Core
{
    /// <summary>Поиск сторонних VPN/TUN-адаптеров, через которые может уходить трафик WARP.</summary>
    public static class NetCheck
    {
        static readonly Regex VpnLike = new Regex(
            @"tun|tap|wintun|wireguard|sing-box|clash|mihomo|v2ray|xray|hiddify|nekoray|amnezia|outline|openvpn|vpn|happ|zerotier|tailscale",
            RegexOptions.IgnoreCase);

        /// <summary>Имена активных VPN-адаптеров, у которых есть шлюз (т.е. они могут забирать весь трафик).</summary>
        public static List<string> ForeignVpnAdapters()
        {
            var res = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    string text = ni.Name + " " + ni.Description;
                    if (text.IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0) continue; // сам WARP
                    bool tunnelType = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp;
                    if (!tunnelType && !VpnLike.IsMatch(text)) continue;
                    bool hasGateway;
                    try { hasGateway = ni.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)); }
                    catch { hasGateway = false; }
                    if (hasGateway) res.Add($"{ni.Name} ({ni.Description})");
                }
            }
            catch { }
            return res;
        }
    }
}

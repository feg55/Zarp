using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Zarp.Core
{
    /// <summary>Поиск сторонних VPN/TUN-адаптеров, через которые может уходить трафик WARP.</summary>
    public static class NetCheck
    {
        static readonly Regex VpnLike = new Regex(
            @"\b(?:tun|tap)\d*\b|wintun|wireguard|sing-box|clash|mihomo|v2ray|xray|hiddify|nekoray|amnezia|outline|openvpn|vpn|happ|zerotier|tailscale",
            RegexOptions.IgnoreCase);
        // Системные механизмы IPv6 имеют тип Tunnel, но не являются сторонними VPN.
        static readonly Regex WindowsIpv6Tunnel = new Regex(
            @"\b(?:teredo|isatap|6to4|6-to-4|6over4)\b",
            RegexOptions.IgnoreCase);

        internal static bool IsForeignVpnAdapter(string name, string description, NetworkInterfaceType type,
            OperationalStatus status, IEnumerable<IPAddress> gateways)
        {
            if (status != OperationalStatus.Up) return false;
            string text = name + " " + description;
            if (text.IndexOf("Cloudflare", StringComparison.OrdinalIgnoreCase) >= 0 || WindowsIpv6Tunnel.IsMatch(text))
                return false;
            bool tunnelType = type == NetworkInterfaceType.Tunnel || type == NetworkInterfaceType.Ppp;
            if (!tunnelType && !VpnLike.IsMatch(text)) return false;
            return gateways != null && gateways.Any(g => g != null && !g.Equals(IPAddress.Any) && !g.Equals(IPAddress.IPv6Any));
        }

        /// <summary>Активные VPN-адаптеры со шлюзом; системные IPv6-туннели не учитываются.</summary>
        public static List<string> ForeignVpnAdapters()
        {
            var res = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (IsForeignVpnAdapter(ni.Name, ni.Description, ni.NetworkInterfaceType, ni.OperationalStatus,
                            ni.GetIPProperties().GatewayAddresses.Select(g => g.Address)))
                            res.Add($"{ni.Name} ({ni.Description})");
                    }
                    catch (NetworkInformationException) { } // исчезнувший адаптер не отменяет проверку остальных
                }
            }
            catch { }
            return res;
        }
    }
}

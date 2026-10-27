
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MohsenShare.Maui.Services.Networking;

public sealed record LocalNetworkRoute(
    string InterfaceId,
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress SubnetMask,
    int PrefixLength,
    IPAddress? BroadcastAddress);

public static class NetworkRouteResolver
{
    public static IReadOnlyList<LocalNetworkRoute>
        GetLocalRoutes()
    {
        var routes = new List<LocalNetworkRoute>();

        foreach (NetworkInterface networkInterface
                 in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus !=
                OperationalStatus.Up)
            {
                continue;
            }

            if (networkInterface.NetworkInterfaceType ==
                NetworkInterfaceType.Loopback ||
                networkInterface.NetworkInterfaceType ==
                NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;

            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast
                     in properties.UnicastAddresses)
            {
                IPAddress address = unicast.Address;

                if (!IsUsableIPv4(address))
                    continue;

                IPAddress mask = unicast.IPv4Mask;

                if (!IsValidMask(mask))
                    continue;

                int prefixLength = GetPrefixLength(mask);

                IPAddress? broadcast =
                    GetBroadcastAddress(address, mask, prefixLength);

                routes.Add(new LocalNetworkRoute(
                    networkInterface.Id,
                    networkInterface.Name,
                    address,
                    mask,
                    prefixLength,
                    broadcast));
            }
        }

        return routes
            .OrderByDescending(x => x.PrefixLength)
            .ToArray();
    }

    /// <summary>
    /// آدرس محلی مناسب برای مقصد را فقط وقتی برمی‌گرداند
    /// که یک مسیر مستقیم و بدون ابهام شناسایی شده باشد.
    /// در غیر این صورت null برمی‌گرداند تا سیستم‌عامل
    /// مسیر را طبق جدول Route انتخاب کند.
    /// </summary>
    public static LocalNetworkRoute?
        FindDirectRoute(IPAddress destination)
    {
        if (destination.AddressFamily !=
            AddressFamily.InterNetwork)
        {
            return null;
        }

        var matches = GetLocalRoutes()
            .Where(route => IsInSameSubnet(
                destination,
                route.LocalAddress,
                route.SubnetMask))
            .OrderByDescending(route => route.PrefixLength)
            .ToList();

        if (matches.Count == 0)
            return null;

        int bestPrefix = matches[0].PrefixLength;

        var bestMatches = matches
            .Where(x => x.PrefixLength == bestPrefix)
            .ToList();

        // اگر چند رابط با بهترین Prefix یکسان وجود داشته باشند،
        // انتخاب حدسی انجام نمی‌دهیم.
        if (bestMatches
            .Select(x => x.LocalAddress)
            .Distinct()
            .Count() != 1)
        {
            return null;
        }

        return bestMatches[0];
    }

    /// <summary>
    /// شبکه‌هایی را برمی‌گرداند که Broadcast زیرشبکه‌ای
    /// معتبر برای کشف دستگاه دارند.
    /// </summary>
    public static IReadOnlyList<LocalNetworkRoute>
        GetBroadcastRoutes()
    {
        return GetLocalRoutes()
            .Where(x =>
                x.BroadcastAddress is not null &&
                x.PrefixLength <= 30)
            .GroupBy(x => new
            {
                LocalAddress = x.LocalAddress.ToString(),
                BroadcastAddress = x.BroadcastAddress!.ToString()
            })
            .Select(x => x.First())
            .ToArray();
    }

    public static bool IsInSameSubnet(
        IPAddress address,
        IPAddress localAddress,
        IPAddress subnetMask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            localAddress.AddressFamily != AddressFamily.InterNetwork ||
            subnetMask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] targetBytes = address.GetAddressBytes();
        byte[] localBytes = localAddress.GetAddressBytes();
        byte[] maskBytes = subnetMask.GetAddressBytes();

        for (int i = 0; i < 4; i++)
        {
            if ((targetBytes[i] & maskBytes[i]) !=
                (localBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();

        // 0.0.0.0/8
        if (bytes[0] == 0)
            return false;

        // آدرس‌های Link-local خودکار
        if (bytes[0] == 169 && bytes[1] == 254)
            return false;

        // Multicast و محدوده‌های رزروشده بالای IPv4
        if (bytes[0] >= 224)
            return false;

        return true;
    }

    private static bool IsValidMask(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] bytes = mask.GetAddressBytes();
        bool foundZero = false;

        foreach (byte value in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool isOne = (value & (1 << bit)) != 0;

                if (!isOne)
                {
                    foundZero = true;
                }
                else if (foundZero)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int GetPrefixLength(IPAddress mask)
    {
        int prefixLength = 0;

        foreach (byte value in mask.GetAddressBytes())
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) == 0)
                    return prefixLength;

                prefixLength++;
            }
        }

        return prefixLength;
    }

    private static IPAddress? GetBroadcastAddress(
        IPAddress address,
        IPAddress mask,
        int prefixLength)
    {
        // /31 و /32 برای Broadcast معمولی مناسب نیستند.
        if (prefixLength > 30)
            return null;

        byte[] addressBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcastBytes = new byte[4];

        for (int i = 0; i < 4; i++)
        {
            broadcastBytes[i] = (byte)(
                addressBytes[i] | (byte)~maskBytes[i]);
        }

        var broadcast = new IPAddress(broadcastBytes);

        if (broadcast.Equals(address) ||
            IPAddress.IsLoopback(broadcast))
        {
            return null;
        }

        return broadcast;
    }
}

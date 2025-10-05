using System.Net.NetworkInformation;
using NonIPWebConfig.Models;

namespace NonIPWebConfig.Services;

/// <summary>
/// Service for discovering and managing network interfaces
/// </summary>
public class NetworkInterfaceService
{
    /// <summary>
    /// Get all available network interfaces
    /// </summary>
    public List<NetworkInterfaceInfo> GetNetworkInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Get MAC address
            var macAddress = string.Join(":", ni.GetPhysicalAddress()
                .GetAddressBytes()
                .Select(b => b.ToString("X2")));

            // Get speed (convert to Mbps)
            long speed = 0;
            try
            {
                speed = ni.Speed / 1_000_000; // Convert to Mbps
            }
            catch
            {
                // Speed might not be available for some interfaces
                speed = 0;
            }

            interfaces.Add(new NetworkInterfaceInfo
            {
                Name = ni.Name,
                Description = ni.Description,
                MacAddress = macAddress,
                Status = ni.OperationalStatus.ToString(),
                Speed = speed
            });
        }

        return interfaces;
    }

    /// <summary>
    /// Get a specific network interface by name
    /// </summary>
    public NetworkInterfaceInfo? GetNetworkInterface(string name)
    {
        return GetNetworkInterfaces().FirstOrDefault(i => i.Name == name);
    }

    /// <summary>
    /// Check if a network interface exists
    /// </summary>
    public bool InterfaceExists(string name)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(ni => ni.Name == name);
    }
}

using NonIPFileDelivery.Models;

namespace NonIPFileDelivery.Services;

public interface IConfigurationService
{
    Configuration LoadConfiguration(string configPath);
    void SaveConfiguration(Configuration config, string configPath);
    bool ValidateConfiguration(Configuration config);
    void CreateDefaultConfiguration(string configPath);
}
using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Storage;

namespace AirenoOS.Revit.Plugin
{
    /// <summary>
    /// Endpoint URL + bearer token are stored on ProjectInformation via ExtensibleStorage.
    /// Token leaves the document only over HTTPS — never logged or written to a side file.
    /// </summary>
    internal static class ConnectionConfig
    {
        private const string DefaultEndpoint = "https://mcp.airenos.io/v1/extract";

        public static void Save(Document doc, string endpoint, string token)
        {
            using var tr = new Transaction(doc, "AirenoOS — save connection config");
            try
            {
                tr.Start();
                AirenoDataStore.Write(doc, endpoint: endpoint, bearer: token);
                tr.Commit();
            }
            catch
            {
                if (tr.HasStarted()) tr.RollBack();
                throw;
            }
        }

        public static (string Endpoint, string Token) Load(Document doc)
        {
            var stored = AirenoDataStore.Read(doc);
            var endpoint = string.IsNullOrWhiteSpace(stored.Endpoint) ? DefaultEndpoint : stored.Endpoint;
            return (endpoint, stored.Bearer ?? string.Empty);
        }
    }
}

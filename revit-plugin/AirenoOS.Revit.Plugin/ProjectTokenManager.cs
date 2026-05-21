using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using AirenoOS.Revit.Plugin.Storage;

namespace AirenoOS.Revit.Plugin
{
    /// <summary>
    /// Manages the document_project_token stored on ProjectInformation via ExtensibleStorage.
    /// File copy detection: if the stored path hash no longer matches the current file
    /// path, the copy gets a fresh token so two files never share one identity.
    /// </summary>
    internal static class ProjectTokenManager
    {
        public static void EnsureProjectToken(Document doc)
        {
            var stored = AirenoDataStore.Read(doc);
            var currentPathHash = GetPathHash(doc.PathName);

            // Already initialised AND path matches → nothing to do.
            if (!string.IsNullOrEmpty(stored.Token) && stored.PathHash == currentPathHash)
                return;

            using var tr = new Transaction(doc, "AirenoOS — initialise project token");
            try
            {
                tr.Start();

                // Same file, missing token: generate.
                // File copy detected (hash mismatch): generate new token for the copy.
                // First time: generate.
                AirenoDataStore.Write(doc, token: Guid.NewGuid().ToString(), pathHash: currentPathHash);

                tr.Commit();
            }
            catch
            {
                if (tr.HasStarted()) tr.RollBack();
                throw;
            }
        }

        public static string GetProjectToken(Document doc)
            => AirenoDataStore.Read(doc).Token;

        private static string GetPathHash(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(path.ToLowerInvariant());
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
        }
    }
}

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityLeanMcp.Mcp;

[McpServerResourceType]
public class UnityResources
{
    /// <summary>
    /// Returns the read-only cheatsheet of built-in UnityLeanMcp.Inspect helper signatures.
    /// </summary>
    [McpServerResource(
        UriTemplate = InspectHelpCatalog.ResourceUri,
        Name = InspectHelpCatalog.ResourceName,
        Title = InspectHelpCatalog.ResourceTitle,
        MimeType = InspectHelpCatalog.ResourceMimeType)]
    [Description(InspectHelpCatalog.ResourceDescription)]
    public string GetInspectHelp() => InspectHelpCatalog.FullHelpText;
}

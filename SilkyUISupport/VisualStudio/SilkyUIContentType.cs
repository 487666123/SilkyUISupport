using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace SilkyUISupport;

internal static class SilkyUIContentType
{
    [Export]
    [Name("SilkyUI XML")]
    [BaseDefinition("xml")]
    internal static readonly ContentTypeDefinition XmlContentType = null!;

    [Export]
    [FileExtension(".sui.xml")]
    [ContentType("SilkyUI XML")]
    internal static readonly FileExtensionToContentTypeDefinition SuiXmlFileExtension = null!;
}

using System.Reflection;

namespace JackcessDotNet;

public sealed class DatabaseHeader
{
    private readonly JetFormat _format;

    private DatabaseHeader(JetFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public static DatabaseHeader Create(JetFormat format)
        => new(format);

    public void WriteTo(PageFile file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));

        // Use embedded empty database templates from Jackcess.
        string resourceName = _format.Version switch
        {
            JetVersion.Jet3 => "JackcessDotNet.Resources.empty.mdb",
            JetVersion.Jet4 => "JackcessDotNet.Resources.empty2003.mdb",
            _ => throw new InvalidOperationException($"Unsupported format {_format.Version}.")
        };

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");

        file.ReplaceWith(stream);
    }
}

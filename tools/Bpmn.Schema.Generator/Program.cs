using Bpmn.Model;
using Bpmn.Schema.Generator;

// Writes the checked-in schema. The generated file is committed so a format change shows up as a
// schema diff in review rather than as an invisible consequence of editing a model class.
//
// The root is found by walking up to the solution file rather than by counting "..' segments out of
// the build output, so the tool keeps working when the target framework or configuration changes the
// depth of that path. A counted walk would not fail there - it would write a schema to the wrong place
// and report success.
var repositoryRoot = args.Length > 0 ? args[0] : FindRepositoryRoot();

var destination = Path.Combine(repositoryRoot, "src", "Bpmn.Model", "schema", BpmnPayloadFormat.SchemaFileName);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

var schema = BpmnSchemaGenerator.Generate();
var unchanged = File.Exists(destination) && File.ReadAllText(destination) == schema;

File.WriteAllText(destination, schema);

Console.WriteLine($"Payload format version : {BpmnPayloadFormat.Version}");
Console.WriteLine($"Schema                 : {destination}");
Console.WriteLine(unchanged ? "Result                 : unchanged" : "Result                 : WRITTEN (review the diff)");

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);

    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bpmn.slnx")))
        directory = directory.Parent;

    return directory?.FullName
        ?? throw new InvalidOperationException(
            "Could not locate the repository root (no Bpmn.slnx above the build output). "
            + "Pass it explicitly: dotnet run --project tools/Bpmn.Schema.Generator -- <root>");
}

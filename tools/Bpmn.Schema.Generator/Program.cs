using Bpmn.Model;
using Bpmn.Schema.Generator;

// Writes the checked-in schema. The generated file is committed so a format change shows up as a
// schema diff in review rather than as an invisible consequence of editing a model class.
var repositoryRoot = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var destination = Path.Combine(repositoryRoot, "src", "Bpmn.Model", "schema", BpmnPayloadFormat.SchemaFileName);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

var schema = BpmnSchemaGenerator.Generate();
var unchanged = File.Exists(destination) && File.ReadAllText(destination) == schema;

File.WriteAllText(destination, schema);

Console.WriteLine($"Payload format version : {BpmnPayloadFormat.Version}");
Console.WriteLine($"Schema                 : {destination}");
Console.WriteLine(unchanged ? "Result                 : unchanged" : "Result                 : WRITTEN (review the diff)");

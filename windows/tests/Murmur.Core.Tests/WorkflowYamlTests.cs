using YamlDotNet.RepresentationModel;

namespace Murmur.Core.Tests;

/// <summary>GitHub rejects a workflow that isn't valid YAML and starts no job, so a broken file fails silently (the first windows-v0.1.0-beta.1
/// tag push ran nothing: a plain scalar held ": "). Parse every workflow here so the mistake shows up in the tests.</summary>
public class WorkflowYamlTests
{
    [Fact]
    public void EveryWorkflowIsValidYaml()
    {
        var dir = Path.Combine(ModelTestSupport.RepoRoot(), ".github", "workflows");
        var files = Directory.EnumerateFiles(dir, "*.yml").Concat(Directory.EnumerateFiles(dir, "*.yaml")).ToList();
        Assert.NotEmpty(files);
        var bad = new List<string>();
        foreach (var f in files)
        {
            try
            {
                var yaml = new YamlStream();
                using var reader = new StreamReader(f);
                yaml.Load(reader);
                if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root || !root.Children.ContainsKey(new YamlScalarNode("jobs")))
                    bad.Add($"{Path.GetFileName(f)}: not a single mapping with jobs");
            }
            catch (YamlDotNet.Core.YamlException e) { bad.Add($"{Path.GetFileName(f)}: {e.Start} {e.Message}"); }
        }
        Assert.True(bad.Count == 0, "Invalid workflow YAML: " + string.Join("; ", bad));
    }
}

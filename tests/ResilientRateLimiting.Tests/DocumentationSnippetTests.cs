using Xunit;

namespace ResilientRateLimiting.Tests;

public class DocumentationSnippetTests
{
    [Fact]
    public void Reads_a_region_and_removes_its_common_indentation()
    {
        var source = "class A\n{\n    // snippet: demo\n    var x = 1;\n        var y = 2;\n    // end-snippet\n}\n";

        var regions = DocumentationSnippets.ReadRegions(source, "A.cs");

        Assert.Equal("var x = 1;\n    var y = 2;", regions["demo"]);
    }

    [Fact]
    public void Treats_windows_and_unix_line_endings_the_same()
    {
        var unix = DocumentationSnippets.ReadRegions("// snippet: demo\nvar x = 1;\n// end-snippet\n", "A.cs");
        var windows = DocumentationSnippets.ReadRegions("// snippet: demo\r\nvar x = 1;  \r\n// end-snippet\r\n", "A.cs");

        Assert.Equal(unix["demo"], windows["demo"]);
    }

    [Fact]
    public void Reports_a_region_without_an_end()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DocumentationSnippets.ReadRegions("// snippet: demo\nvar x = 1;\n", "A.cs"));

        Assert.Contains("demo", error.Message);
    }

    [Fact]
    public void Reports_a_code_block_without_a_snippet_marker()
    {
        var doc = "Text.\n\n```csharp\nvar x = 1;\n```\n";

        var errors = DocumentationSnippets.CheckDocument(doc, "page.md", new Dictionary<string, string>());

        Assert.Single(errors);
        Assert.Contains("page.md", errors[0]);
    }

    [Fact]
    public void Reports_a_code_block_that_differs_from_its_region()
    {
        var doc = "<!-- snippet: demo -->\n```csharp\nvar x = 2;\n```\n";
        var regions = new Dictionary<string, string> { ["demo"] = "var x = 1;" };

        var errors = DocumentationSnippets.CheckDocument(doc, "page.md", regions);

        Assert.Single(errors);
        Assert.Contains("demo", errors[0]);
    }

    [Fact]
    public void Accepts_a_matching_block_and_ignores_other_languages()
    {
        var doc = "<!-- snippet: demo -->\n\n```csharp\r\nvar x = 1;\r\n```\n\n```bash\ndotnet test\n```\n";
        var regions = new Dictionary<string, string> { ["demo"] = "var x = 1;" };

        Assert.Empty(DocumentationSnippets.CheckDocument(doc, "page.md", regions));
    }

    [Fact]
    public void Reports_an_unknown_snippet_name()
    {
        var doc = "<!-- snippet: missing -->\n```csharp\nvar x = 1;\n```\n";

        var errors = DocumentationSnippets.CheckDocument(doc, "page.md", new Dictionary<string, string>());

        Assert.Contains("missing", Assert.Single(errors));
    }

    [Fact]
    public void Every_code_block_in_the_documentation_is_quoted_from_a_sample()
    {
        var errors = DocumentationSnippets.FindErrors(DocumentationSnippets.FindRepoRoot());

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}

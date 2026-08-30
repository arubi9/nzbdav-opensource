using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Tests.Queue.PostProcessors;

public class RenameDuplicatesPostProcessorTests
{
    [Fact]
    public void RenameDuplicates_TreatsNamesCaseInsensitively()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new DavDatabaseContext(options);
        var parent = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "movies", null, DavItem.ItemType.Directory, null, null);
        var first = DavItem.New(Guid.NewGuid(), parent, "proof.JPG", 10, DavItem.ItemType.NzbFile, null, null);
        var second = DavItem.New(Guid.NewGuid(), parent, "proof.jpg", 10, DavItem.ItemType.NzbFile, null, null);
        context.Items.AddRange(parent, first, second);

        new RenameDuplicatesPostProcessor(new DavDatabaseClient(context)).RenameDuplicates();

        Assert.Equal("proof.JPG", first.Name);
        Assert.Equal("proof (2).JPG", second.Name);
        Assert.Equal("/content/movies/proof (2).JPG", second.Path);
    }
}

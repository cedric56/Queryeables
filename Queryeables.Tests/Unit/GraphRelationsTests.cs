using Queryeables.Abstraction;
using Queryeables.Tests.Graphs;

namespace Queryeables.Tests.Unit;

/// <summary>
/// The relations of an application of management, through the graphs the generator writes from the attributes: several
/// levels, one to one, many to many, references, a tree; loading a list, saving only what changed, filtering through them.
/// </summary>
public sealed class GraphRelationsTests
{
    private readonly MemoryDatabase _database = new();

    private IEntityStoreFactory Stores => new EntityStoreFactory(() => new MemoryStore(_database));

    private GraphRepository<GInvoice, long> Invoices => new(Stores, GInvoiceGraph.Create());

    private async Task<(GTag Urgent, GTag Paid, GClient Paris)> SeedAsync()
    {
        await using var store = new MemoryStore(_database);
        var urgent = new GTag { Name = "urgent" };
        var paid = new GTag { Name = "paid" };
        var paris = new GClient { Name = "Ana", City = "Paris" };
        await store.InsertAsync(urgent);
        await store.InsertAsync(paid);
        await store.InsertAsync(paris);
        return (urgent, paid, paris);
    }

    private static GInvoice Invoice(string number, GClient? client, params GTag[] tags) => new()
    {
        Number = number,
        Year = 2026,
        GClientId = client?.Id,
        Lines =
        [
            new GInvoiceLine { Product = "tea", Quantity = 2, Price = 3m, Taxes = [new GLineTax { Rate = 0.2m }, new GLineTax { Rate = 0.05m }] },
            new GInvoiceLine { Product = "cake", Quantity = 1, Price = 5m, Taxes = [] },
        ],
        Note = new GInvoiceNote { Text = $"note of {number}" },
        Tags = [.. tags],
    };

    [Fact]
    public async Task Every_level_and_kind_of_relation_is_saved_loaded_and_removed_with_its_parent()
    {
        var (urgent, _, paris) = await SeedAsync();
        var invoice = Invoice("A1", paris, urgent);
        Assert.True((await Invoices.SaveAsync(invoice)).Successful);

        Assert.Equal(2, _database.All<GInvoiceLine>().Count);
        Assert.Equal(2, _database.All<GLineTax>().Count);                    // the second level
        Assert.All(_database.All<GLineTax>(), t => Assert.Equal(invoice.Lines![0].Id, t.GInvoiceLineId));
        Assert.Equal(invoice.Id, Assert.Single(_database.All<GInvoiceNote>()).GInvoiceId);
        Assert.Equal((invoice.Id, urgent.Id), Assert.Single(_database.All<GInvoiceTag>()) is var link ? (link.GInvoiceId, link.GTagId) : default);

        var loaded = new GInvoice { Id = invoice.Id, GClientId = paris.Id };
        await Invoices.LoadChildrenAsync(loaded);
        Assert.Equal(["cake", "tea"], loaded.Lines!.Select(l => l.Product).Order());
        Assert.Equal(2, loaded.Lines!.Single(l => l.Product == "tea").Taxes!.Count);
        Assert.Equal("note of A1", loaded.Note!.Text);
        Assert.Equal("urgent", Assert.Single(loaded.Tags!).Name);
        Assert.Equal("Paris", loaded.GClient!.City);

        Assert.True((await Invoices.RemoveAsync(loaded)).Successful);
        Assert.Empty(_database.All<GInvoice>());
        Assert.Empty(_database.All<GInvoiceLine>());
        Assert.Empty(_database.All<GLineTax>());
        Assert.Empty(_database.All<GInvoiceNote>());
        Assert.Empty(_database.All<GInvoiceTag>());
        Assert.Equal(2, _database.All<GTag>().Count);                         // the linked entities stay
        Assert.Single(_database.All<GClient>());
    }

    [Fact]
    public async Task One_child_set_to_null_is_deleted_and_links_follow_the_collection()
    {
        var (urgent, paid, _) = await SeedAsync();
        var invoice = Invoice("A1", null, urgent);
        await Invoices.SaveAsync(invoice);

        invoice.Note = null;                                                   // NullDeletes = true
        invoice.Tags = [paid];
        Assert.True((await Invoices.SaveAsync(invoice)).Successful);

        Assert.Empty(_database.All<GInvoiceNote>());
        Assert.Equal(paid.Id, Assert.Single(_database.All<GInvoiceTag>()).GTagId);
    }

    [Fact]
    public async Task A_list_is_loaded_with_one_query_per_relation_whatever_its_length()
    {
        var (urgent, paid, paris) = await SeedAsync();
        for (var i = 0; i < 30; i++)
            await Invoices.SaveAsync(Invoice($"N{i}", paris, urgent, paid));
        await using var store = new MemoryStore(_database);
        var invoices = await store.GetAllAsync<GInvoice>();

        _database.Queries = 0;
        await Invoices.LoadChildrenAsync(invoices);

        // Lines, their taxes, the note, the links and their tags, the client: 6 queries for 30 invoices, not 30 times 6.
        Assert.Equal(6, _database.Queries);
        Assert.All(invoices, i => Assert.Equal((2, 2, 2, "Paris"), (i.Lines!.Count, i.Lines.Sum(l => l.Taxes!.Count), i.Tags!.Count, i.GClient!.City)));
    }

    [Fact]
    public async Task Saving_a_graph_writes_only_the_children_that_changed()
    {
        await SeedAsync();
        var invoice = Invoice("A1", null);
        await Invoices.SaveAsync(invoice);
        _database.Log.Clear();

        invoice.Lines![1].Quantity = 3;
        await Invoices.SaveAsync(invoice);

        var updated = Assert.Single(_database.Log, l => l.StartsWith("UPDATE GInvoiceLine", StringComparison.Ordinal));
        Assert.Equal($"UPDATE GInvoiceLine {invoice.Lines[1].Id}", updated);
        Assert.False(_database.Log.Any(l => l.Contains("GLineTax", StringComparison.Ordinal)));      // the taxes did not change
    }

    [Fact]
    public async Task Two_users_changing_different_lines_of_one_invoice_do_not_conflict()
    {
        await SeedAsync();
        await Invoices.SaveAsync(Invoice("A1", null));
        var id = Assert.Single(_database.All<GInvoice>()).Id;
        GInvoice Read() => new() { Id = id, Number = "A1", Year = 2026 };
        var ana = Read();
        var bob = Read();
        await Invoices.LoadChildrenAsync(ana);
        await Invoices.LoadChildrenAsync(bob);

        ana.Lines!.Single(l => l.Product == "tea").Quantity = 10;
        Assert.True((await Invoices.SaveAsync(ana)).Successful);
        bob.Lines!.Single(l => l.Product == "cake").Quantity = 7;              // Bob's copy of the tea line is stale
        var saved = await Invoices.SaveAsync(bob);

        Assert.True(saved.Successful, saved.Exception);
        Assert.Equal((10, 7), (_database.All<GInvoiceLine>().Single(l => l.Product == "tea").Quantity,
            _database.All<GInvoiceLine>().Single(l => l.Product == "cake").Quantity));
    }

    [Fact]
    public async Task Two_users_changing_the_same_line_conflict()
    {
        await SeedAsync();
        await Invoices.SaveAsync(Invoice("A1", null));
        var id = Assert.Single(_database.All<GInvoice>()).Id;
        var ana = new GInvoice { Id = id, Number = "A1" };
        var bob = new GInvoice { Id = id, Number = "A1" };
        await Invoices.LoadChildrenAsync(ana);
        await Invoices.LoadChildrenAsync(bob);

        ana.Lines![0].Quantity = 10;
        await Invoices.SaveAsync(ana);
        bob.Lines![0].Quantity = 11;
        var saved = await Invoices.SaveAsync(bob);

        Assert.Equal(EntityErrorCode.Conflict, saved.ErrorCode);
    }

    [Fact]
    public async Task A_tree_has_as_many_levels_as_its_rows()
    {
        var tasks = new GraphRepository<GTask, long>(Stores, GTaskGraph.Create());
        var root = new GTask
        {
            Title = "root",
            SubTasks = [new GTask { Title = "a", SubTasks = [new GTask { Title = "a1", SubTasks = [new GTask { Title = "a1x" }] }] }, new GTask { Title = "b" }],
        };
        Assert.True((await tasks.SaveAsync(root)).Successful);
        Assert.Equal(5, _database.All<GTask>().Count);

        var loaded = new GTask { Id = root.Id, Title = "root" };
        await tasks.LoadChildrenAsync(loaded);
        Assert.Equal("a1x", loaded.SubTasks!.Single(t => t.Title == "a").SubTasks![0].SubTasks![0].Title);

        Assert.True((await tasks.RemoveAsync(loaded)).Successful);
        Assert.Empty(_database.All<GTask>());
    }

    // ---------- Filters through the relations ----------

    private async Task<IReadOnlyList<string>> NumbersAsync(System.Linq.Expressions.Expression<Func<GInvoice, bool>> predicate)
    {
        await using var store = new MemoryStore(_database);
        return [.. (await store.SelectAsync(predicate)).Select(i => i.Number).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_filter_goes_through_collections_navigations_and_links()
    {
        var (urgent, paid, paris) = await SeedAsync();
        await using (var store = new MemoryStore(_database))
            await store.InsertAsync(new GClient { Name = "Bob", City = "Lyon" });
        var lyon = _database.All<GClient>().Single(c => c.City == "Lyon");
        await Invoices.SaveAsync(Invoice("A", paris, urgent));
        var b = Invoice("B", lyon, paid);
        b.Lines = [new GInvoiceLine { Product = "tea", Quantity = 5, Taxes = [] }];
        await Invoices.SaveAsync(b);
        await Invoices.SaveAsync(new GInvoice { Number = "C", Lines = [], Tags = [] });
        var product = "cake";

        Assert.Equal(["A"], await NumbersAsync(i => i.Lines!.Any(l => l.Product == product)));
        Assert.Equal(["C"], await NumbersAsync(i => !i.Lines!.Any()));
        Assert.Equal(["B", "C"], await NumbersAsync(i => i.Lines!.All(l => l.Quantity > 1)));
        Assert.Equal(["A"], await NumbersAsync(i => i.Lines!.Count() >= 2));
        Assert.Equal(["B", "C"], await NumbersAsync(i => i.Lines!.Count() < 2));
        Assert.Equal(["A"], await NumbersAsync(i => i.GClient!.City == "Paris"));
        Assert.Equal(["C"], await NumbersAsync(i => i.GClient == null));
        Assert.Equal(["B"], await NumbersAsync(i => i.Tags!.Any(t => t.Name == "paid") && i.Year == 2026));
        Assert.Equal(["A", "B"], await NumbersAsync(i => i.Note!.Text.StartsWith("note")));
        Assert.Equal(["A"], await NumbersAsync(i => i.Lines!.Any(l => l.Taxes!.Any(t => t.Rate == 0.05m))));      // two levels down
    }

    [Fact]
    public async Task A_condition_mixing_a_navigation_and_a_column_is_refused()
    {
        await SeedAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => NumbersAsync(i => i.GClient!.Name == i.Number));
    }
}

---
_layout: landing
title: Queryeables
---

<div class="qy-hero"><div class="qy-hero-inner">
<div>
<span class="qy-kicker">.NET 10 · local or remote · AOT-ready</span>
<h1>Your data, <em>the same code</em> everywhere.</h1>
<p>One store API over SQLite, PostgreSQL, SQL Server, EF Core or a remote server. Caches that follow the database, parent-child graphs saved in one transaction, and change notifications between clients.</p>
<div class="qy-cta">
<a class="qy-primary" href="getting-started.md">Get started</a>
<a class="qy-ghost" href="introduction.md">Is it for you?</a>
<a class="qy-ghost" href="https://github.com/cedric56/Queryeables">GitHub</a>
</div>
</div>
<div class="qy-code">
<input type="radio" name="qy-mode" id="qy-local" checked>
<input type="radio" name="qy-mode" id="qy-remote">
<div class="qy-tabs"><label for="qy-local">Local database</label><label for="qy-remote">Remote server</label></div>
<div class="qy-panes">
<div class="qy-pane qy-local"><pre><code><span class="k">[EntityCache]</span>                         <span class="c">// follows the database</span>
public class <span class="t">Person</span>
{
    public long Id { get; set; }
    <span class="k">[EntityGraph]</span> public List&lt;<span class="t">Contact</span>&gt;? Contacts { get; set; }
}
<span></span>
services.UseQueryeables(q =&gt; q
<span class="hl">    .UseSqlite(<span class="s">"Data Source=app.db"</span>)</span>
    .AddGeneratedEntities());
<span></span>
<span class="k">var</span> page = <span class="k">await</span> stores.Query&lt;<span class="t">Person</span>&gt;()
    .Where(p =&gt; p.Name.StartsWith(search))
    .OrderBy(p =&gt; p.Name).Skip(<span class="s">50</span>).Take(<span class="s">25</span>)
    .ToListAsync();</code></pre></div>
<div class="qy-pane qy-remote"><pre><code><span class="k">[EntityCache]</span>                         <span class="c">// follows the server</span>
public class <span class="t">Person</span>
{
    public long Id { get; set; }
    <span class="k">[EntityGraph]</span> public List&lt;<span class="t">Contact</span>&gt;? Contacts { get; set; }
}
<span></span>
services.UseQueryeables(q =&gt; q
<span class="hl">    .UseMagicOnion(<span class="s">"https://server:5001"</span>)</span>
    .AddGeneratedEntities());
<span></span>
<span class="k">var</span> page = <span class="k">await</span> stores.Query&lt;<span class="t">Person</span>&gt;()
    .Where(p =&gt; p.Name.StartsWith(search))
    .OrderBy(p =&gt; p.Name).Skip(<span class="s">50</span>).Take(<span class="s">25</span>)
    .ToListAsync();                  <span class="c">// sorted and paged by the server</span></code></pre></div>
</div>
<footer>One line changes. The rest of the application does not.</footer>
</div>
</div>
<div class="qy-flow" aria-hidden="true">
<svg viewBox="0 0 1000 170" xmlns="http://www.w3.org/2000/svg">
<path id="qy-w1" class="wire" d="M230 85 H400"/>
<path id="qy-w2" class="wire" d="M600 85 C680 85 700 40 790 40"/>
<path id="qy-w3" class="wire" d="M600 85 C680 85 700 130 790 130"/>
<rect class="node" x="10" y="45" width="220" height="80" rx="14"/>
<text class="label" x="120" y="80" text-anchor="middle">Database</text>
<text class="sub" x="120" y="102" text-anchor="middle">SQLite · PostgreSQL · SQL Server</text>
<rect class="node" x="400" y="45" width="200" height="80" rx="14"/>
<text class="label" x="500" y="80" text-anchor="middle">Server</text>
<text class="sub" x="500" y="102" text-anchor="middle">policies · graphs · caches</text>
<rect class="node" x="790" y="10" width="190" height="60" rx="12"/>
<text class="label" x="885" y="37" text-anchor="middle">Client cache</text>
<text class="sub" x="885" y="56" text-anchor="middle">desktop · mobile</text>
<rect class="node" x="790" y="100" width="190" height="60" rx="12"/>
<text class="label" x="885" y="127" text-anchor="middle">Client cache</text>
<text class="sub" x="885" y="146" text-anchor="middle">browser · AOT</text>
<text class="sub" x="315" y="72" text-anchor="middle">row changed</text>
<text class="sub" x="695" y="88" text-anchor="middle">notified</text>
<circle class="dot" r="5"><animateMotion dur="2.4s" repeatCount="indefinite"><mpath href="#qy-w1"/></animateMotion></circle>
<circle class="dot" r="5"><animateMotion dur="2.4s" begin="1.2s" repeatCount="indefinite"><mpath href="#qy-w2"/></animateMotion></circle>
<circle class="dot" r="5"><animateMotion dur="2.4s" begin="1.2s" repeatCount="indefinite"><mpath href="#qy-w3"/></animateMotion></circle>
</svg>
</div>
</div>

<div class="qy-stats">
<div><b>4×</b><span>faster reads by key than Dommel with the same predicate (262 → 64 µs)</span></div>
<div><b>1 line</b><span>from a local database to a remote server</span></div>
<div><b>0</b><span>SQL or expression trees over the wire: a whitelist of operators</span></div>
<div><b>9</b><span>compile-time diagnostics for the attributes (QRY001–QRY009)</span></div>
</div>

<ul class="qy-providers">
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.Sqlite/README.md">SQLite</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.PostgreSql/README.md">PostgreSQL</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.Dommel/README.md">SQL Server · MySQL (Dommel)</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.EntityFramework/README.md">EF Core</a></li>
<li><a href="guides/remote-access.md">MagicOnion</a></li>
<li><a href="guides/change-notifications.md">SignalR</a></li>
</ul>

<div class="qy-grid">
<div><h3>One store API</h3><p>CRUD, filters with <code>&amp;&amp;</code>, <code>||</code> and <code>!</code>, sorting, paging, projections and transactions: identical on Dommel, EF Core and a remote server, proven by the same contract tests.</p></div>
<div><h3>Caches that follow the database</h3><p><code>[EntityCache]</code>: row by row from SQLite hooks, PostgreSQL <code>LISTEN/NOTIFY</code> or SQL Server; bursts coalesced, incremental resync after a reconnection.</p></div>
<div><h3>Entity graphs</h3><p><code>[EntityGraph]</code>: a parent saved, removed and loaded with its children in one transaction, navigation properties set, lookups found in their caches.</p></div>
<div><h3>Results, not exceptions</h3><p>Transactions return a <code>DbResult</code> with an error kind: <code>Validation</code>, <code>Conflict</code>, <code>NotFound</code>, <code>Forbidden</code>.</p></div>
<div><h3>Remote without coupling</h3><p>The remote core speaks to <code>IEntityRemote</code>; MagicOnion is one transport. An access policy per exposed type, validation on the server.</p></div>
<div><h3>Generated, AOT-ready</h3><p>A source generator writes the graph services and the registration; a Native AOT smoke app is published and run by the CI.</p></div>
</div>

<div class="qy-fit">
<div class="yes"><h3>Choose Queryeables for</h3><ul>
<li>an application whose data is local today and shared through a server tomorrow;</li>
<li>clients that must see each other's changes;</li>
<li>parent-child aggregates saved as one unit.</li>
</ul></div>
<div><h3>Choose something else for</h3><ul>
<li>offline-first writes: <a href="https://github.com/CommunityToolkit/Datasync">Datasync</a>;</li>
<li>fine-grained reactive computations: <a href="https://github.com/ActualLab/Fusion">Fusion</a>;</li>
<li>joins and reporting: EF Core or Dapper, next to Queryeables.</li>
</ul></div>
</div>

- [Introduction](introduction.md): the model, and an honest comparison.
- [Getting started](getting-started.md): a working application in five steps.
- [Guides](guides/stores-and-transactions.md): stores, queries, graphs, caches, remote access, notifications, security.
- [Benchmarks](benchmarks.md): what the abstraction costs, measured.
- [API reference](api/Queryeables.html): every public type.

> **Status:** early. The API may still change before 1.0.

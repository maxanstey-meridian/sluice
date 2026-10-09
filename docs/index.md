---
layout: home
hero:
  name: Sluice
  text: Your reads are your invalidation rules
  tagline: A .NET cache that knows what each cached value was built from. Change some data, and only the values that read it are thrown away. FusionCache does the caching.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: EF Core
      link: /guides/ef-core
features:
  - title: Name each thing that can change, once
    details: A Source names something a cached value can depend on, such as one user's greeting. Reads record it and writes clear it from the same declaration, so the two sides can't disagree on a name.
  - title: Cached values record what they read
    details: read.From(dependency, fetch) records what it reads and fetches it in one call. With EF Core, you write ordinary queries, and Sluice records the tables each one reads.
  - title: Clearing waits for the commit
    details: With EF Core, a save or bulk update clears what it changed once it commits, with no Sluice code. With anything else, you call InvalidateAsync after your write commits.
---

Sluice is a .NET library that runs inside your app. [FusionCache](https://github.com/ZiggyCreatures/FusionCache)
does the actual caching: in memory, in Redis, and keeping several servers in sync. Sluice adds
the record of what each cached value read, and clears the right values when that data changes.

```text
Name each thing that can change, once.
A cached value records what it read while it's built.
A write clears the cached values that read what it changed.
Clearing waits until the write has committed.
```

Start with [Getting Started](/getting-started), then read
[Sources and Queries](/guides/sources-and-queries) for how declarations work.
[Limitations](/misc/limitations) lists what Sluice deliberately doesn't do.

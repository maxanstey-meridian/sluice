---
layout: home
hero:
  name: Sluice
  text: Your reads are your invalidation rules
  tagline: Cached values record what they read while they compute. A write evicts the values that read what it changed, and nothing else. FusionCache does the caching.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: EF Core
      link: /guides/ef-core
features:
  - title: Declare each dependency once
    details: A Source names a family of things a value can depend on, with a typed key. Readers record it and writers invalidate it, so the two sides can't disagree on a name.
  - title: Computes record what they read
    details: read.From(dependency, fetch) records a dependency and fetches in one call. With EF Core, read.Entity, read.Children and read.All take the dependency from the model.
  - title: Invalidation waits for the commit
    details: Generic writes call InvalidateAsync once they have committed. EF writes need nothing; Sluice invalidates after the transaction commits.
---

Sluice is a .NET library, not a service. It runs in your process over a FusionCache of its own,
so the memory and distributed levels, the backplane, stampede protection and serialisation are
FusionCache's. Sluice adds the dependency tracking.

```text
Declare each dependency once.
Computes record what they read.
Writes invalidate what they changed.
Invalidation waits for the commit.
```

Start with [Getting Started](/getting-started), then read
[Sources and Queries](/guides/sources-and-queries) for how declarations work.
[Limitations](/misc/limitations) lists what Sluice deliberately doesn't do.

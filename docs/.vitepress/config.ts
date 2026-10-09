import { defineConfig } from "vitepress";

export default defineConfig({
  title: "Sluice",
  description: "Dependency-tracking invalidation for FusionCache, with optional EF Core integration",
  base: "/sluice/",

  themeConfig: {
    nav: [
      { text: "Get Started", link: "/getting-started" },
      { text: "Guides", link: "/guides/sources-and-queries" },
      { text: "Configuration", link: "/guides/configuration" },
      { text: "NuGet", link: "https://www.nuget.org/packages/Meridian.Sluice" },
    ],

    sidebar: [
      {
        text: "Introduction",
        items: [
          { text: "What is Sluice?", link: "/" },
          { text: "Getting Started", link: "/getting-started" },
        ],
      },
      {
        text: "Guides",
        items: [
          { text: "Sources and Queries", link: "/guides/sources-and-queries" },
          { text: "Reading and Invalidating", link: "/guides/invalidation" },
          { text: "EF Core", link: "/guides/ef-core" },
          { text: "Strict Reads", link: "/guides/strict-reads" },
          { text: "Transactions", link: "/guides/transactions" },
          { text: "Configuration", link: "/guides/configuration" },
        ],
      },
      {
        text: "Misc",
        items: [
          { text: "How It Works", link: "/misc/how-it-works" },
          { text: "Performance", link: "/misc/performance" },
          { text: "Limitations", link: "/misc/limitations" },
        ],
      },
    ],

    socialLinks: [{ icon: "github", link: "https://github.com/maxanstey-meridian/sluice" }],

    search: {
      provider: "local",
    },
  },
});

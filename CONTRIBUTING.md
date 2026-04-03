# Contributing to Caching.NET

Thank you for your interest in contributing to Caching.NET. This document explains how to get set up, run tests, and submit changes.

## Overview

Caching.NET is a shared .NET caching package providing **InMemory**, **Redis**, and **Hybrid** modes behind a single **ICacheService** abstraction. Contributions that keep the API stable, improve reliability, or add well-scoped features are welcome.

## Repository structure

- **src/Caching.NET** – main library (ICacheService, options, services, extensions, health, telemetry).
- **tests/Caching.NET.Tests** – unit tests (xUnit).
- **samples/Caching.NET.Sample** – sample ASP.NET Core app using the package.
- **docs/** – implementation details, operations, and other documentation.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or later).
- Git.
- A fork of the repository (for pull requests).

## Getting started

1. **Clone the repository**

   ```bash
   git clone https://github.com/baps-apps/caching-net.git
   cd caching-net
   ```

   If you use a fork:

   ```bash
   git remote add upstream https://github.com/baps-apps/caching-net.git
   ```

2. **Restore and build**

   ```bash
   dotnet restore
   dotnet build
   ```

3. **Run tests**

   ```bash
   dotnet test
   ```

   All tests must pass before submitting a pull request.

## Development workflow

- **Branch:** Create a feature or bugfix branch from the default branch (e.g. `main`).
- **Code style:** The solution uses **CodeStyle.NET** and central package management (`Directory.Packages.props`). Keep formatting and analyzer rules satisfied (fix any build or IDE warnings).
- **API stability:** Prefer extending via **configuration** (`CacheOptions`), **per-call options** (`CacheCallOptions`), and **extension methods** rather than new members on `ICacheService`. See [docs/INTERNALS.md](docs/INTERNALS.md) for versioning and compatibility.
- **Tests:** Add or update tests in `tests/Caching.NET.Tests` for new behavior or bug fixes. Use the existing patterns (xUnit, `ServiceCollection`/`IConfiguration` for DI tests).
- **Docs:** Update [README.md](README.md) and [docs/INTERNALS.md](docs/INTERNALS.md) (and [docs/OPERATIONS.md](docs/OPERATIONS.md) if relevant) when changing configuration, behavior, or public API.

## Submitting changes

1. **Commit:** Use clear, concise commit messages. Prefer present tense (e.g. “Add validation for CacheOptions when Enabled is true”).
2. **Changelog:** For user-visible changes, add an entry under **[Unreleased]** in [CHANGELOG.md](CHANGELOG.md) (Added / Changed / Fixed / Removed as appropriate).
3. **Pull request:** Open a PR against the upstream default branch. Describe the change, link any related issues, and confirm that `dotnet build` and `dotnet test` pass.

## Versioning and releases

- The project follows [Semantic Versioning](https://semver.org/):
  - **MAJOR** – breaking API or behavior changes.
  - **MINOR** – backwards-compatible features or options.
  - **PATCH** – bug fixes and internal improvements.
- Package version is set in [src/Caching.NET/Caching.NET.csproj](src/Caching.NET/Caching.NET.csproj). Release process and tagging are maintained by the maintainers. See [docs/INTERNALS.md](docs/INTERNALS.md#versioning-and-compatibility) for full versioning policy.

## Questions and issues

- **Bugs and feature requests:** Open an issue in the repository with a clear description and steps to reproduce (for bugs).
- **Security:** Do not report security-sensitive issues in public issues; contact the maintainers through the repository’s preferred channel.

Thank you for contributing.

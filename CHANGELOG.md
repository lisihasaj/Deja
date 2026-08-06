# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Initial extraction of the `Query<T>` and `Mutation<T>` primitives:
  - `Query<T>` with bindable `InitialLoading` / `IsLoading` / `IsReFetching` / `IsError` /
    `ErrorMessage` / `Data` / `ReFetchCount` state, in-flight deduplication by `QueryKey`,
    supersede-and-cancel of stale executions, cancellation-aware fetch functions, a one-shot
    retry for browser-tab-freeze `HttpClient` timeouts, and success / error / settled callbacks.
  - `Mutation<T>` with bindable `IsLoading` / `IsError` / `ErrorMessage` / `Data` state and
    success / error / settled callbacks, including typed and `void` mutation functions.
  - `DisplayUserException` for errors whose message is safe to show to the end user, routed to
    dedicated `OnDisplayUserError` callbacks.

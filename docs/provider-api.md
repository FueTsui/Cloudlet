# Provider configuration API (stable)

Namespace `RcloneLink.Core`, net10.0. Additions to `RcloneService`:

```csharp
Task<IReadOnlyList<RcloneProvider>> GetProvidersAsync(CancellationToken cancellationToken=default);
Task<ProviderConfigurationSession> BeginProviderConfigurationAsync(string name, string provider, IReadOnlyDictionary<string,string> parameters, CancellationToken cancellationToken=default, bool openBrowser=true);
```

```csharp
class RcloneProvider {
 string Name, Description, Prefix;
 List<RcloneProviderOption> Options;
 bool Hide;
}
class RcloneProviderOption {
 string Name, Help, Type, Provider;
 JsonElement Default;
 string DefaultStr;
 string DefaultText { get; } // DefaultStr or scalar Default, ready for UI
 bool Required, IsPassword, Sensitive, Advanced, Exclusive;
 int Hide;
 List<RcloneProviderExample> Examples;
}
class RcloneProviderExample { string Value, Help, Provider; }
class ProviderConfigurationStep {
 string State, Error;
 RcloneProviderOption? Option;
 bool Complete { get; }
}
class ProviderConfigurationSession : IAsyncDisposable {
 string Name { get; }
 string Provider { get; }
 ProviderConfigurationStep CurrentStep { get; }
 bool IsCommitted { get; }
 string? AuthorizationUrl { get; } // last validated localhost fallback link, if any
 event EventHandler<string>? AuthorizationUrlAvailable; // only validated localhost OAuth browser authorization URLs; never generic logs
 Task<ProviderConfigurationStep> AdvanceAsync(string result, CancellationToken cancellationToken=default);
 Task CommitAsync(CancellationToken cancellationToken=default);
 ValueTask DisposeAsync();
}
```

Begin configures common form values in an isolated temporary rclone config using authenticated loopback RC non-interactive config/create. It returns the first post-config question, or Complete=true for providers without post-config questions. Advance responds to CurrentStep.Option and drives config/update --continue. Required fields are submitted by the initial form, so the flow does not ask every option individually. All password values remain inside request bodies; never CLI arguments or returned diagnostic logs.

OAuth: answering the native config_is_local question affirmatively uses rclone's normal browser authorization. The async Advance may stay pending while the browser grants authorization; cancellation cancels/disposes the isolated process. UI should show an awaiting authorization state, keep its Cancel button enabled, and optionally provide the AuthorizationUrlAvailable localhost fallback link. openBrowser=false prevents automatic browser launch using a child-only rclone remote environment override, while preserving the same pending flow and fallback link; it is useful for manual browser handling or isolated protocol tests. Other config questions are rendered using the returned metadata. Provider option/example `Provider` conditions use case-sensitive comma-separated exact names or a leading ! negation; conditions or selected provider empty mean match. Hide & 2 marks fields hidden from configuration; Hide & 1 alone must not hide them.

Commit requires Complete=true, then merges only the target INI section into the real config. Existing config gets a same-directory backup. Duplicate remote names and config changes since Begin are rejected; no overwrite prompt or silent replacement. Cancel/dispose deletes the temporary draft and leaves the original config unchanged. Encrypted source configs cannot be merged safely and produce an explicit error. State machine does not support backtracking; UI may cancel and restart with its retained initial form values. Serialize operations per session; concurrent Advance/Commit are rejected. UI should dispose the active session on Cancel/Close and block runtime config path changes until the wizard closes.

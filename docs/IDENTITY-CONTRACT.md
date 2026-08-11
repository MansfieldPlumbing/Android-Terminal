# Terminal Identity Contract

**Status:** architectural baseline; provider registration and broker implementation not yet complete
**Providers:** Google and Microsoft
**Relying-party domain:** `mansfieldplumbing.dev`
**Protected boundary:** Kestrel-hosted Terminal services and BOS capability requests

**Human-facing promise:** [Our Pledge](OUR-PLEDGE.md)

Authentication establishes who is present. It does not itself authorize torch, storage, shell, process, listener or administrative capability.

---

## 1. Authority Chain

```text
Google / Microsoft
    authenticate an external identity
        |
        v
mansfieldplumbing.dev relying-party boundary
    static public client owns provider callback and branded sign-in ceremony
        |
        v
Terminal authentication boundary
    validates provider-signed identity evidence and Terminal challenge binding
    maps it to TerminalPrincipalId
        |
        v
BOS policy
    grants explicit, scoped capabilities
        |
        v
Kestrel endpoint / worker operation
```

Google and Microsoft do not grant Terminal capabilities. `mansfieldplumbing.dev` does not become BOS. Kestrel does not infer authorization merely because authentication succeeded.

Terminal has no mandatory online identity. Local interactive use requires no provider and no Mansfield Plumbing account.

---

## 2. Stable Identity

The durable external key is provider-qualified subject identity:

```text
ExternalIdentity
    ProviderId
    Issuer
    Subject
    TenantId?       # when materially required by Microsoft policy

TerminalPrincipal
    TerminalPrincipalId
    LinkedExternalIdentities[]
    LocalDisplayName?
    CapabilityPolicyRef
```

Email address, provider display name and avatar are neither requested nor retained by default. If a later user-facing feature genuinely needs one, it requires an explicit scope and privacy-contract amendment. They are mutable presentation metadata, never account keys and never proof of account linkage.

Google and Microsoft identities remain distinct until the already-authenticated user explicitly links them. Matching email strings must not merge accounts.

---

## 3. Sign-In Ceremony

Native authentication uses the system browser and Authorization Code flow with PKCE. Do not collect provider passwords, use the resource-owner-password flow, use implicit flow or host provider login in Terminal's WebView.

Every authorization transaction requires:

- high-entropy PKCE verifier and S256 challenge;
- exact redirect-URI matching;
- unguessable `state` bound to the initiating Terminal authentication session;
- OIDC `nonce` where an ID token is requested;
- one-time authorization-code redemption;
- issuer, audience, signature, expiry and not-before validation;
- provider-specific tenant validation where configured;
- bounded timeouts and terminal failure states.

The Android client, public web relying party and any future desktop client use separately registered provider clients. A mobile application contains no confidential client secret.

### Static landing-page responsibility

`mansfieldplumbing.dev` may remain a static public client with no confidential backend and no client secret. It can host the branded provider choice, initiate Authorization Code + PKCE through separately registered Google and Microsoft public clients, receive the browser callback and complete the handoff to Terminal.

The static page is not an issuer. It cannot mint a trustworthy Mansfield Plumbing identity token because every secret embedded in it is public. Google or Microsoft remains the cryptographic issuer; Terminal validates that provider's signed identity evidence directly and then creates its own local session.

The handoff must bind the provider result to a one-time, high-entropy challenge created by the receiving Terminal instance:

```text
Terminal authentication session
provider issuer and audience
provider subject
intended Terminal device/service
expiry
one-time state/nonce
```

Provider evidence and authorization codes must not appear in query strings. A same-device Android App Link/deep-link ceremony and a remote-browser-to-local-Kestrel ceremony are different transports and require separate executable receipts. In particular, the remote path must prove its behavior under browser mixed-content, CORS and private-network-access rules rather than assuming that a public HTTPS page can always call a LAN HTTP endpoint.

A future confidential broker may replace this handoff without changing `TerminalPrincipalId` or BOS authorization semantics, but it is not required for the first receipt.

### Maintained door versus owner-managed ingress

Mansfield Plumbing maintains one public web-facing sign-in ceremony:

```text
mansfieldplumbing.dev
    -> Google or Microsoft
    -> provider-signed identity evidence
    -> Terminal local session
```

It is the supported red carpet, not a mandatory gateway. A device owner may deliberately configure another authentication or transport boundary, including local pairing, client certificates, a private overlay network, an owner-operated reverse proxy or a future admitted authentication package.

Owner-managed ingress:

- is named distinctly in settings and status;
- cannot impersonate the Mansfield-maintained portal;
- receives no hidden Mansfield Plumbing relay or data path;
- declares its listener, principal mapping and capability policy;
- remains independently revocable;
- is not represented as Mansfield-operated security or availability.

Alternative ingress changes authentication mechanics, not BOS authorization. Every resulting principal still receives only explicit local capability grants.

---

## 4. No-Collection Portal

The Mansfield Plumbing portal is an authentication ceremony, not an account service or analytics surface.

It has:

```text
no Mansfield Plumbing user database
no server-side identity session
no analytics or telemetry
no advertising or tracking pixels
no behavioral profiling or fingerprinting
no email capture or mailing list
no provider-token logging
no cloud copy of TerminalPrincipalId
```

PKCE verifier, `state`, `nonce` and provider response material may exist transiently in browser memory or origin-scoped session storage only for the active ceremony. They are cleared on success, failure or timeout and are never placed in URLs, analytics events or durable browser storage.

Terminal stores the minimum local identity mapping required to recognize an explicitly retained principal:

```text
ProviderId
Issuer
Subject
local TerminalPrincipalId
local policy and revocation state
```

That mapping stays on the device unless the user explicitly exports it. Ephemeral sign-in may discard it when the local session ends.

Google and Microsoft necessarily process authentication at their own endpoints under their own policies. Static hosting, DNS and network providers may produce infrastructure logs outside Terminal's control. The privacy statement must distinguish that third-party processing from Mansfield Plumbing collection and must not promise that the Internet itself produces no logs.

---

## 5. Token Containment

Provider access and refresh tokens terminate at the authentication boundary.

They must not enter:

```text
PowerShell objects or history
BOS command text
Surface XML
logs, crash reports or notifications
router frames
package environment variables
URLs or query strings
general Kestrel endpoint handlers
```

Device-held private keys and refresh material use Android Keystore-backed protection where available. Server-side credentials remain server-side. Logs may record `TerminalPrincipalId`, provider name, decision and correlation identity, but not tokens or authorization codes.

Terminal requests only the `openid` identity scope by default. `profile`, `email`, Google Drive, Microsoft Graph and other delegated scopes are not part of login. Every additional scope requires a named user-facing feature, explicit consent and its own capability/privacy review.

---

## 6. Authentication Is Not Authorization

Every protected request resolves through this sequence:

```text
authenticated session
    -> TerminalPrincipalId
    -> endpoint operation
    -> requested BOS capability
    -> resource and rights
    -> policy decision
    -> generation-qualified grant/lease
```

Examples:

```text
signed in
    != may open PowerShell
    != may read Downloads
    != may bind a LAN listener
    != may toggle torch
    != may install cargo
```

Remote principals receive a smaller default policy than the local interactive owner. Destructive, privacy-sensitive or persistent operations may require an on-device confirmation even for an authenticated owner.

An endpoint calls a semantic BOS operation with the authenticated principal and correlation identity. It never receives an Android adapter, raw `Context`, `CapabilityLease` belonging to another principal or ambient PowerShell runspace authority.

---

## 7. Kestrel Session Boundary

Kestrel owns protocol mechanics:

```text
TLS and HTTP
cookies or proof-bound session credentials
request size/time limits
anti-forgery policy where browser cookies are used
rate limits
authentication middleware
endpoint routing
```

BOS owns semantic authorization. Remedy owns server-worker mortality.

LAN and remote listeners default to authenticated access. A health endpoint may be anonymous only when its output is constant, contains no device identity or operational detail and the listener's exposure policy explicitly permits it.

Browser sessions use secure, HTTP-only cookies with an explicit SameSite policy when the topology permits. API tokens are sent in authorization headers, never URLs. Long-lived bearer credentials are avoided; a future proof-of-possession design may bind sessions to a device/client key.

---

## 8. Revocation and Recovery

Revocation is layered:

```text
provider revocation
    stops future provider refresh/authentication as defined by provider

Terminal sign-out
    destroys local authentication sessions and protected token material

principal policy revocation
    rejects new BOS grants and retires affected leases

job cancellation
    stops Kestrel endpoint/job work through Remedy quiescence
```

Provider logout alone must not be assumed to revoke every already-issued local session. Terminal maintains its own session registry, expirations and explicit "sign out all devices/sessions" operation.

Process restart never reconstructs authority solely from a username, email, stale cookie or numeric identifier. Persisted sessions require intact cryptographic evidence and current local policy.

---

## 9. First Identity Receipt

The first receipt proves identity without yet granting dangerous capabilities:

```text
open system browser
    -> Google or Microsoft Authorization Code + PKCE
    -> validated callback/handoff
    -> TerminalPrincipalId established
    -> authenticated Kestrel /whoami returns that principal

negative receipts
    wrong state rejected
    wrong nonce rejected
    wrong issuer rejected
    wrong audience rejected
    expired handoff rejected
    replayed code/handoff rejected
    Google and Microsoft accounts with matching email remain distinct
    authentication grants no BOS capability by itself
    sign-out invalidates the local session
    no token appears in logs, PowerShell or notifications
    portal emits no analytics, telemetry or durable identity storage
    only issuer + subject identity is retained locally by default
```

Only after this receipt is green should a remote principal request a low-risk BOS capability. Storage, shell launch, package admission and administrative operations require separate receipts.

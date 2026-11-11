# Code signing (Azure Artifact Signing)

Signing puts a verified publisher name on `NotchWin.exe`, so Chrome and Windows SmartScreen stop treating it as "unknown software from an unknown publisher". It uses **Azure Artifact Signing** (formerly *Trusted Signing*): about **US$9.99/month** (Basic, 5,000 signatures), with the private key kept in Microsoft's HSMs and nothing to store in GitHub except IDs.

> **Eligibility.** Public Trust certificates for **individual developers** are only available in the **United States and Canada**. **Organizations** can apply from the US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway and Israel. See [Microsoft's prerequisites](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart#prerequisites). If you're not eligible, see [Alternatives](#alternatives).

> **Reputation still matters.** Signing doesn't remove every warning on day one. SmartScreen reputation builds up as the signed file gets downloaded, but warnings drop off much faster than for unsigned files.

Until you complete the setup below, the `release` workflow still works; it just publishes unsigned builds.

## One-time setup (≈30 min plus identity check)

### 1. Azure account and signing account

1. Create an Azure account at <https://portal.azure.com> (a pay-as-you-go subscription is required).
2. For an **individual** validation, check **Cost Management + Billing → Billing profile**: the account type must be *Individual*, and the legal name and address must exactly match your government ID.
3. In the portal, search **Subscriptions → your subscription → Resource providers**, find `Microsoft.CodeSigning`, then click **Register**.
4. Search **Artifact Signing accounts → Create**:
   - Resource group: new, e.g. `notchwin-signing`
   - Name: e.g. `notchwin-signing`
   - Region: pick one close to you and **note its endpoint** (e.g. East US → `https://eus.codesigning.azure.net/`, West Europe → `https://weu.codesigning.azure.net/`; [full list](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart#create-an-artifact-signing-account))
   - Pricing tier: **Basic**

### 2. Verify your identity

1. Open the signing account → **Access control (IAM) → Add role assignment** and give yourself **Artifact Signing Identity Verifier**.
2. **Identity validations → New identity → Public** (choose *Individual* or *Organization*), fill in the form and **Create**.
3. When the status shows **Action Required**, follow the link and complete the ID check (government photo ID, sometimes a proof of address).
4. Wait for the status to become **Completed**.

### 3. Certificate profile

**Certificate profiles → Create → Public Trust**, name it e.g. `notchwin`, and select the validated identity.

### 4. Let GitHub Actions sign (OIDC, no passwords)

1. **Microsoft Entra ID → App registrations → New registration**, name `notchwin-github-signing`. Note the **Application (client) ID** and **Directory (tenant) ID**.
2. In that app: **Certificates & secrets → Federated credentials → Add credential → GitHub Actions deploying Azure resources**:
   - Organization: `latishkumeriya`, Repository: `Windows-Notch`
   - Entity type: **Environment**, name: `release`
3. Back on the signing account: **Access control (IAM) → Add role assignment → Artifact Signing Certificate Profile Signer**, then assign it to `notchwin-github-signing`.

### 5. Configure the GitHub repository

1. **Settings → Environments → New environment** → `release`. Optionally restrict deployment to tags matching `v*`.
2. **Settings → Secrets and variables → Actions → Variables → New repository variable**, and add:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | Application (client) ID from step 4 |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Your Azure subscription ID |
| `AZURE_SIGNING_ENDPOINT` | Region endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | Signing account name, e.g. `notchwin-signing` |
| `AZURE_SIGNING_PROFILE` | Certificate profile name, e.g. `notchwin` |

None of these are secrets; access is controlled by the federated credential plus the `release` environment.

## Publishing a signed release

```powershell
git tag v0.3.0
git push origin v0.3.0
```

The **release** workflow builds the exe, signs it, checks the signature (fails the run if it isn't `Valid`) and attaches `NotchWin.exe` to a GitHub Release with its SHA-256. Download it from the repo's **Releases** page.

Check a downloaded file on Windows: right-click `NotchWin.exe` → **Properties → Digital Signatures**, or

```powershell
Get-AuthenticodeSignature .\NotchWin.exe | Format-List Status, SignerCertificate
```

## Alternatives

If you aren't eligible for Artifact Signing:

- **Build it yourself** (free, no warnings): files you compile locally aren't downloaded through a browser, so they don't get the download warning. See *Build from source* in the README.
- **Buy an OV code-signing certificate** from a CA such as Certum, Sectigo or SSL.com. These are available worldwide (Certum's open-source developer certificate is among the cheapest), but the key lives on a USB token or the CA's cloud service, so CI signing needs that vendor's tooling.
- **SignPath Foundation** signs open-source projects for free, but the repository must be public and meet their OSS criteria.
- A **self-signed** certificate does *not* help: Chrome and SmartScreen ignore it.

# Third-party notices

The `SqlBranch` .NET tool bundles the libraries below. The `SqlBranch.Testing`
package references the direct dependencies marked **(direct)** as NuGet
packages, which bring their own dependencies. Each library keeps its own
license; the license texts are in each NuGet package and at the project links.
Versions are those of release 0.2.1.

## MIT License

| Library | Version | Project |
|---|---|---|
| Azure.Storage.Blobs **(direct)** | 12.30.1 | https://github.com/Azure/azure-sdk-for-net |
| Azure.Storage.Common | 12.29.0 | https://github.com/Azure/azure-sdk-for-net |
| Azure.Core | 1.61.0 | https://github.com/Azure/azure-sdk-for-net |
| System.ClientModel | 1.15.0 | https://github.com/Azure/azure-sdk-for-net |
| Microsoft.Data.SqlClient **(direct)** | 7.1.2 | https://github.com/dotnet/SqlClient |
| Microsoft.Data.SqlClient.Extensions.Abstractions | 7.1.2 | https://github.com/dotnet/SqlClient |
| Microsoft.Data.SqlClient.Internal.Logging | 7.1.2 | https://github.com/dotnet/SqlClient |
| Microsoft.SqlServer.Server | 1.0.0 | https://github.com/dotnet/SqlClient |
| Microsoft.Identity.Client | 4.87.0 | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.Identity.Client.Extensions.Msal | 4.84.2 | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.IdentityModel.* and System.IdentityModel.Tokens.Jwt | 8.22.0 to 8.23.0 | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.Bcl.*, Microsoft.Extensions.*, System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.IO.Hashing, System.Memory.Data, System.Security.Cryptography.*, System.Threading.RateLimiting | 9.0 to 11.0 | https://github.com/dotnet/runtime |
| System.CommandLine **(direct, tool only)** | 3.0.0 | https://github.com/dotnet/command-line-api |
| YamlDotNet **(direct)** | 18.1.0 | https://github.com/aaubry/YamlDotNet |
| ZstdSharp.Port **(direct)** | 0.8.8 | https://github.com/oleg-st/ZstdSharp |

## Apache License 2.0

| Library | Version | Project |
|---|---|---|
| AWSSDK.S3 **(direct)** | 4.1.2 | https://github.com/aws/aws-sdk-net |
| AWSSDK.Core | 4.1.1.1 | https://github.com/aws/aws-sdk-net |
| AWSSDK.Extensions.Numerics | 4.1.0 | https://github.com/aws/aws-sdk-net |
| Apache.Arrow | 22.1.0 | https://arrow.apache.org/ |

## Microsoft Software License Terms

| Library | Version | Project |
|---|---|---|
| Microsoft.Data.SqlClient.SNI.runtime (native, Windows only) | 7.1.2 | https://github.com/dotnet/SqlClient |

This native library is distributed by Microsoft with Microsoft.Data.SqlClient
under the "Microsoft Software License Terms for Microsoft.Data.SqlClient.SNI
Library", included as `LICENSE.txt` in its NuGet package.

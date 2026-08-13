using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Tests.Clients.Usenet.Caching;
using NzbWebDAV.Utils;
using Xunit;
using backend.Tests.Config;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection("EnvironmentVariableCollection")]
public sealed class SetupGrantServiceTests
{
	private sealed class RevocationCapturingJellyfinHandler : HttpMessageHandler
	{
		public int RevokeCalls { get; private set; }

		public string? RevokedToken { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Method == HttpMethod.Post)
			{
				Uri requestUri = request.RequestUri;
				if ((object)requestUri != null && string.Equals(requestUri.AbsolutePath, "/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase))
				{
					string content = JsonSerializer.Serialize(new
					{
						AccessToken = "session-token",
						User = new
						{
							Policy = new
							{
								IsAdministrator = true
							}
						}
					});
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent(content, Encoding.UTF8, "application/json")
					});
				}
			}
			if (request.Method == HttpMethod.Post)
			{
				Uri requestUri2 = request.RequestUri;
				if ((object)requestUri2 != null && string.Equals(requestUri2.AbsolutePath, "/Sessions/Logout", StringComparison.OrdinalIgnoreCase))
				{
					RevokeCalls++;
					if (request.Headers.TryGetValues("X-Emby-Token", out IEnumerable<string> values))
					{
						RevokedToken = values.FirstOrDefault();
					}
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
				}
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	private sealed class RotationRecoveryJellyfinHandler : HttpMessageHandler
	{
		private int _authenticateCalls;

		public bool FailPreviousLogout { get; set; }

		public List<string> LogoutTokens { get; } = new List<string>();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Method == HttpMethod.Post)
			{
				Uri requestUri = request.RequestUri;
				if ((object)requestUri != null && requestUri.AbsolutePath == "/Users/AuthenticateByName")
				{
					string accessToken = ((Interlocked.Increment(ref _authenticateCalls) == 1) ? "token-a" : "token-b");
					string content = JsonSerializer.Serialize(new
					{
						AccessToken = accessToken,
						User = new
						{
							Policy = new
							{
								IsAdministrator = true
							}
						}
					});
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
					{
						Content = new StringContent(content, Encoding.UTF8, "application/json")
					});
				}
			}
			if (request.Method == HttpMethod.Post)
			{
				Uri requestUri = request.RequestUri;
				if ((object)requestUri != null && requestUri.AbsolutePath == "/Sessions/Logout")
				{
					IEnumerable<string> values;
					string text = (request.Headers.TryGetValues("X-Emby-Token", out values) ? (values.FirstOrDefault() ?? string.Empty) : string.Empty);
					LogoutTokens.Add(text);
					if (FailPreviousLogout && text == "token-a")
					{
						return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
					}
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
				}
			}
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	private sealed class JellyfinAuthenticationHandler(Func<int, (bool Administrator, string Token)> responseFactory, TimeSpan? delay = null) : HttpMessageHandler
	{
		private int _call;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			int num;
			if (!(request.Method != HttpMethod.Post))
			{
				Uri requestUri = request.RequestUri;
				if ((object)requestUri != null)
				{
					num = ((!string.Equals(requestUri.AbsolutePath, "/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
					goto IL_005e;
				}
			}
			num = 1;
			goto IL_005e;
			IL_005e:
			if (num != 0)
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}
			TimeSpan? timeSpan = delay;
			if (timeSpan.HasValue)
			{
				await Task.Delay(delay.Value, cancellationToken);
			}
			int call = Interlocked.Increment(ref _call) - 1;
			(bool, string) tuple = responseFactory(call);
			bool administrator = tuple.Item1;
			string token = tuple.Item2;
			string body = JsonSerializer.Serialize(new
			{
				AccessToken = token,
				User = new
				{
					Policy = new
					{
						IsAdministrator = administrator
					}
				}
			});
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			};
		}
	}

	private sealed class LatchingJellyfinAuthenticationHandler(Func<int, (bool Administrator, string Token)> responseFactory, TaskCompletionSource authStarted, TaskCompletionSource authAllowed, TimeSpan? delay = null) : HttpMessageHandler
	{
		private readonly TaskCompletionSource _authStarted = authStarted;

		private readonly TaskCompletionSource _authAllowed = authAllowed;

		private readonly Func<int, (bool Administrator, string Token)> _responseFactory = responseFactory;

		private readonly TimeSpan? _delay = delay;

		private int _call;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			int num;
			if (!(request.Method != HttpMethod.Post))
			{
				Uri requestUri = request.RequestUri;
				if ((object)requestUri != null)
				{
					num = ((!string.Equals(requestUri.AbsolutePath, "/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase)) ? 1 : 0);
					goto IL_0069;
				}
			}
			num = 1;
			goto IL_0069;
			IL_0069:
			if (num != 0)
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}
			TimeSpan? delay = _delay;
			if (delay.HasValue)
			{
				await Task.Delay(_delay.Value, cancellationToken);
			}
			_authStarted.TrySetResult();
			await _authAllowed.Task;
			int call = Interlocked.Increment(ref _call) - 1;
			(bool, string) tuple = _responseFactory(call);
			bool administrator = tuple.Item1;
			string token = tuple.Item2;
			string body = JsonSerializer.Serialize(new
			{
				AccessToken = token,
				User = new
				{
					Policy = new
					{
						IsAdministrator = administrator
					}
				}
			});
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			};
		}
	}

	[Fact("SetupGrantServiceTests.cs", 22)]
	public async Task IssueAsync_CreatesAdminAccountAndGrant()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-issue-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigEncryptionService encryption = new ConfigEncryptionService();
			ConfigManager configManager = new ConfigManager(encryption);
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			SetupGrantResult result = await service.IssueAsync("Admin", "secret", CancellationToken.None);
			Account account = await context.Accounts.SingleAsync((Account x) => (int)x.Type == 1 && x.Username == "admin");
			Assert.True(PasswordUtil.Verify(account.PasswordHash, "secret", account.RandomSalt));
			Assert.NotEqual<string>("secret", account.PasswordHash);
			Assert.NotEqual<string>(actual: (await context.SetupGrants.SingleAsync()).GrantedTokenHash, expected: result.Grant);
			ConfigItem setupTokenRow = await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key");
			Assert.True(setupTokenRow.IsEncrypted);
			Assert.NotEqual<string>("token", setupTokenRow.ConfigValue);
			ConfigManager restartedConfig = new ConfigManager(new ConfigEncryptionService());
			await restartedConfig.LoadConfig();
			Assert.Equal("token", restartedConfig.GetSetupJellyfinApiKey());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 61)]
	public async Task IssueAsync_ReusesExistingAdminAccountWithoutDuplicate()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-reuse-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			string existingSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
			context.Accounts.Add(new Account
			{
				Type = Account.AccountType.Admin,
				Username = "admin",
				RandomSalt = existingSalt,
				PasswordHash = PasswordUtil.Hash("secret", existingSalt)
			});
			await context.SaveChangesAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			await service.IssueAsync("admin", "secret", CancellationToken.None);
			Assert.Single(await context.Accounts.Where((Account x) => (int)x.Type == 1 && x.Username == "admin").ToListAsync());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 95)]
	public async Task IssueAsync_IdempotentWithoutCiphertextChurn_ForSameJellyfinToken()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-idempotent-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "stable"));
			SetupGrantResult first = await service.IssueAsync("admin", "secret", CancellationToken.None);
			string firstCiphertext = (await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key")).ConfigValue;
			SetupGrantResult second = await service.IssueAsync("admin", "secret", CancellationToken.None);
			string secondCiphertext = (await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key")).ConfigValue;
			Assert.Equal(firstCiphertext, secondCiphertext);
			Assert.False(await service.ValidateAsync(first.Grant, CancellationToken.None));
			Assert.True(await service.ValidateAsync(second.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 123)]
	public async Task IssueAsync_UpdatesCiphertextWhenJellyfinTokenRotates()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-rotate-token-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-a"));
			await service.IssueAsync("admin", "secret", CancellationToken.None);
			string firstCiphertext = (await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key")).ConfigValue;
			SetupGrantService secondService = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-b"));
			SetupGrantResult secondResult = await secondService.RenewAsync("admin", "secret", CancellationToken.None);
			string secondCiphertext = (await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key")).ConfigValue;
			Assert.NotEqual(firstCiphertext, secondCiphertext);
			ConfigManager restartedConfig = new ConfigManager(new ConfigEncryptionService());
			await restartedConfig.LoadConfig();
			Assert.Equal("token-b", restartedConfig.GetSetupJellyfinApiKey());
			Assert.True(await secondService.ValidateAsync(secondResult.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 155)]
	public async Task IssueAsync_RejectsNonAdministrator()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-not-admin-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: false));
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.IssueAsync("admin", "secret", CancellationToken.None));
			Assert.False(await context.Accounts.AnyAsync());
			Assert.False(await context.SetupGrants.AnyAsync());
			Assert.False(await context.ConfigItems.AnyAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 178)]
	public async Task IssueAsync_RejectsWrongLocalAdminPassword()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-password-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			string existingSalt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
			context.Accounts.Add(new Account
			{
				Type = Account.AccountType.Admin,
				Username = "admin",
				RandomSalt = existingSalt,
				PasswordHash = PasswordUtil.Hash("correct", existingSalt)
			});
			await context.SaveChangesAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.IssueAsync("admin", "wrong", CancellationToken.None));
			List<Account> accounts = await context.Accounts.Where((Account x) => (int)x.Type == 1 && x.Username == "admin").ToListAsync();
			Assert.Single(accounts);
			Assert.True(PasswordUtil.Verify(accounts[0].PasswordHash, "correct", accounts[0].RandomSalt));
			Assert.False(await context.SetupGrants.AnyAsync());
			Assert.False(await context.ConfigItems.AnyAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 216)]
	public async Task IssueAsync_CancellationDoesNotPersistPartialWrites()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-cancel-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token", TimeSpan.FromSeconds(1L)));
			using CancellationTokenSource cancellationSource = new CancellationTokenSource();
			Task<SetupGrantResult> issueTask = service.IssueAsync("admin", "secret", cancellationSource.Token);
			await Task.Delay(25);
			cancellationSource.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => issueTask);
			Assert.False(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1 && x.Username == "admin"));
			Assert.False(await context.SetupGrants.AnyAsync());
			Assert.False(await context.ConfigItems.AnyAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 249)]
	public async Task IssueAsync_RevokesSessionTokenWhenPersistenceFailsAfterAuthentication()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-revoke-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			context.ConfigItems.Add(new ConfigItem
			{
				ConfigName = "setup.jellyfin-api-key",
				ConfigValue = "old-session",
				IsEncrypted = false
			});
			await context.SaveChangesAsync();
			RevocationCapturingJellyfinHandler handler = new RevocationCapturingJellyfinHandler();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: true), null, delegate
			{
				throw new InvalidOperationException("forced persistence failure");
			});
			await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueAsync("admin", "secret"));
			Assert.Equal(1, handler.RevokeCalls);
			Assert.Equal("session-token", handler.RevokedToken);
			Assert.False(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1 && x.Username == "admin"));
			ConfigManager persistedSetupManager = new ConfigManager(new ConfigEncryptionService());
			await persistedSetupManager.LoadConfig();
			Assert.Equal("old-session", persistedSetupManager.GetSetupJellyfinApiKey());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 290)]
	public async Task RenewAsync_RotatesGrantAndInvalidatesPrevious()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-renew-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			SetupGrantResult first = await service.IssueAsync("admin", "secret", CancellationToken.None);
			SetupGrantResult second = await service.RenewAsync("admin", "secret", CancellationToken.None);
			Assert.NotEqual(first.Grant, second.Grant);
			Assert.False(await service.ValidateAsync(first.Grant, CancellationToken.None));
			Assert.True(await service.ValidateAsync(second.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 315)]
	public async Task ValidateAsync_RejectsExpiredAndTamperedGrant()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-validate-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			SetupGrantResult issued = await service.IssueAsync("admin", "secret", CancellationToken.None);
			SetupGrant grant = await context.SetupGrants.SingleAsync();
			grant.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1.0);
			await context.SaveChangesAsync();
			Assert.False(await service.ValidateAsync(issued.Grant, CancellationToken.None));
			grant.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15.0);
			grant.GrantedTokenHash = "corrupted";
			await context.SaveChangesAsync();
			Assert.False(await service.ValidateAsync(issued.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 344)]
	public async Task RevokeAsync_InvalidatesGrant()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-revoke-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			SetupGrantResult issued = await service.IssueAsync("admin", "secret", CancellationToken.None);
			await service.RevokeAsync(CancellationToken.None);
			Assert.True((await context.SetupGrants.SingleAsync()).IsRevoked);
			Assert.False(await service.ValidateAsync(issued.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 369)]
	public async Task RevokeAsync_Phase1PersistenceFailureLeavesSessionForRestartRecovery()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-revoke-phase1-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			RevocationCapturingJellyfinHandler handler = new RevocationCapturingJellyfinHandler();
			try
			{
				await using DavDatabaseContext context = await CreateMigratedContextAsync();
				ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
				await manager.LoadConfig();
				SetupGrantService service = new SetupGrantService(context, manager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false));
				SetupGrantResult issued = await service.IssueAsync("admin", "secret");
				await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_setup_revocation_intent BEFORE INSERT ON ConfigItems WHEN NEW.ConfigName = 'setup.revocation-pending' BEGIN SELECT RAISE(ABORT, 'phase1 blocked'); END;");
				await Assert.ThrowsAsync<DbUpdateException>(() => service.RevokeAsync());
				Assert.Equal(0, handler.RevokeCalls);
				Assert.False(await service.IsRevocationPendingAsync());
				Assert.True(await service.ValidateAsync(issued.Grant));
				await using DavDatabaseContext restartedContext = await CreateMigratedContextAsync();
				ConfigManager restartedManager = new ConfigManager(new ConfigEncryptionService());
				await restartedManager.LoadConfig();
				SetupGrantService restarted = new SetupGrantService(restartedContext, restartedManager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false));
				Assert.False(await restarted.IsRevocationPendingAsync());
				Assert.Equal("session-token", restartedManager.GetSetupJellyfinApiKey());
			}
			finally
			{
				handler.Dispose();
			}
		}
	}

	[Fact("SetupGrantServiceTests.cs", 421)]
	public async Task IssueAsync_InitialDurableTokenReadFailureDoesNotRevokeAuthenticatedToken()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-read-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
			await manager.LoadConfig();
			context.ConfigItems.AddRange(new ConfigItem
			{
				ConfigName = "setup.jellyfin-api-key",
				ConfigValue = "old-session",
				IsEncrypted = false
			}, new ConfigItem
			{
				ConfigName = "setup.jellyfin-api-key".ToUpperInvariant(),
				ConfigValue = "ambiguous-session",
				IsEncrypted = false
			});
			await context.SaveChangesAsync();
			RevocationCapturingJellyfinHandler handler = new RevocationCapturingJellyfinHandler();
			try
			{
				SetupGrantService service = new SetupGrantService(context, manager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false));
				await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueAsync("admin", "secret"));
				Assert.Equal(0, handler.RevokeCalls);
				Assert.False(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1));
				Assert.False(await context.SetupGrants.AnyAsync());
			}
			finally
			{
				handler.Dispose();
			}
		}
	}

	[Fact("SetupGrantServiceTests.cs", 471)]
	public async Task RenewAsync_SameTokenPreCommitFailureDoesNotRevokePriorSession()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-same-token-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			RevocationCapturingJellyfinHandler handler = new RevocationCapturingJellyfinHandler();
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
			await manager.LoadConfig();
			Func<HttpClient> factory = () => new HttpClient(handler, disposeHandler: false);
			await new SetupGrantService(context, manager, TimeProvider.System, factory).IssueAsync("admin", "secret");
			SetupGrantService failed = new SetupGrantService(context, manager, TimeProvider.System, factory, null, delegate
			{
				throw new InvalidOperationException("before commit");
			});
			await Assert.ThrowsAsync<InvalidOperationException>(() => failed.RenewAsync("admin", "secret"));
			Assert.Equal(0, handler.RevokeCalls);
			Assert.False((await context.SetupGrants.SingleAsync()).IsRevoked);
			Assert.False(await failed.IsRevocationPendingAsync());
			handler.Dispose();
		}
	}

	[Fact("SetupGrantServiceTests.cs", 507)]
	public async Task RenewAsync_LogoutFailureLeavesNewGrantAndRestartRecoveryCleansPreviousSession()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-recovery-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			RotationRecoveryJellyfinHandler handler = new RotationRecoveryJellyfinHandler();
			try
			{
				await using (DavDatabaseContext context = await CreateMigratedContextAsync())
				{
					ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
					await manager.LoadConfig();
					SetupGrantService service = new SetupGrantService(httpClientFactory: () => new HttpClient(handler, disposeHandler: false), dbContext: context, configManager: manager, timeProvider: TimeProvider.System);
					await service.IssueAsync("admin", "secret");
					handler.FailPreviousLogout = true;
					SetupGrantResult rotated = await service.RenewAsync("admin", "secret");
					Assert.NotEmpty(rotated.Grant);
					Assert.True(rotated.RevocationPending);
					Assert.Contains("cleanup is pending", rotated.Warning, StringComparison.OrdinalIgnoreCase);
					Assert.True(await service.ValidateAsync(rotated.Grant));
					Assert.True(await service.IsRevocationPendingAsync());
					SetupGrant durableGrant = await context.SetupGrants.SingleAsync();
					Assert.NotEqual<string>(string.Empty, durableGrant.GrantedTokenHash);
					Assert.False(durableGrant.IsRevoked);
				}
				await using (DavDatabaseContext verifyContext = await CreateMigratedContextAsync())
				{
					Assert.False((await verifyContext.SetupGrants.SingleAsync()).IsRevoked);
					Assert.True((await verifyContext.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.revocation-pending")).IsEncrypted);
					Assert.True(await verifyContext.ConfigItems.AnyAsync((ConfigItem x) => x.ConfigName == "setup.revocation-pending-token"));
				}
				handler.FailPreviousLogout = false;
				await using (DavDatabaseContext restartedContext = await CreateMigratedContextAsync())
				{
					ConfigManager restartedManager = new ConfigManager(new ConfigEncryptionService());
					await restartedManager.LoadConfig();
					SetupGrantService restartedService = new SetupGrantService(restartedContext, restartedManager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false));
					Assert.True(await restartedService.RecoverPendingAsync());
					Assert.False(await restartedService.IsRevocationPendingAsync());
					Assert.Equal("token-b", restartedManager.GetSetupJellyfinApiKey());
				}
				Assert.Equal(2, handler.LogoutTokens.Count((string token) => token == "token-a"));
			}
			finally
			{
				handler.Dispose();
			}
		}
	}

	[Fact("SetupGrantServiceTests.cs", 575)]
	public async Task RenewAsync_Phase3FailureReturnsUsableGrantAndRecoveryCleansPendingSession()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-phase3-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			RotationRecoveryJellyfinHandler handler = new RotationRecoveryJellyfinHandler();
			try
			{
				await using DavDatabaseContext context = await CreateMigratedContextAsync();
				ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
				await manager.LoadConfig();
				SetupGrantService service = new SetupGrantService(context, manager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false));
				await service.IssueAsync("admin", "secret");
				await context.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_setup_cleanup BEFORE DELETE ON ConfigItems WHEN OLD.ConfigName IN ('setup.revocation-pending', 'setup.revocation-pending-token') BEGIN SELECT RAISE(ABORT, 'phase3 cleanup blocked'); END;");
				SetupGrantResult rotated = await service.RenewAsync("admin", "secret");
				Assert.NotEmpty(rotated.Grant);
				Assert.True(rotated.RevocationPending);
				Assert.NotNull(rotated.Warning);
				Assert.True(await service.ValidateAsync(rotated.Grant));
				Assert.True(await service.IsRevocationPendingAsync());
				await context.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_setup_cleanup");
				Assert.True(await service.RecoverPendingAsync());
				Assert.False(await service.IsRevocationPendingAsync());
			}
			finally
			{
				handler.Dispose();
			}
		}
	}

	[Fact("SetupGrantServiceTests.cs", 622)]
	public async Task SetupCompleted_PreventsIssueAndRenew()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-completed-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using (DavDatabaseContext bootstrapContext = await CreateMigratedContextAsync())
			{
				bootstrapContext.ConfigItems.Add(new ConfigItem
				{
					ConfigName = "setup.completed",
					ConfigValue = "true",
					IsEncrypted = false
				});
				await bootstrapContext.SaveChangesAsync();
			}
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			await Assert.ThrowsAsync<BadHttpRequestException>(() => service.IssueAsync("admin", "secret", CancellationToken.None));
			await Assert.ThrowsAsync<BadHttpRequestException>(() => service.RenewAsync("admin", "secret", CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 654)]
	public async Task ValidateAsync_RejectsGrantWhenSetupCompletesAfterIssue()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-validate-completion-race-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext issueContext = await CreateMigratedContextAsync();
			ConfigManager serviceManager = new ConfigManager(new ConfigEncryptionService());
			await serviceManager.LoadConfig();
			SetupGrantService service = new SetupGrantService(issueContext, serviceManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			SetupGrantResult issued = await service.IssueAsync("admin", "secret", CancellationToken.None);
			await using (DavDatabaseContext completionContext = await CreateMigratedContextAsync())
			{
				(await completionContext.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.completed")).ConfigValue = "true";
				await completionContext.SaveChangesAsync();
			}
			Assert.False(await service.ValidateAsync(issued.Grant, CancellationToken.None));
		}
	}

	[Fact("SetupGrantServiceTests.cs", 686)]
	public async Task ConcurrentIssueAndRenewCallsKeepOnlyOneValidGrantAndMatchPersistedToken_SQLite()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-concurrent-sqlite-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext seedContext = await CreateMigratedContextAsync();
			ConfigManager serviceConfig1 = new ConfigManager(new ConfigEncryptionService());
			ConfigManager serviceConfig2 = new ConfigManager(new ConfigEncryptionService());
			await serviceConfig1.LoadConfig();
			await serviceConfig2.LoadConfig();
			await using DavDatabaseContext serviceContext1 = new DavDatabaseContext();
			await using DavDatabaseContext serviceContext2 = new DavDatabaseContext();
			SetupGrantService service1 = new SetupGrantService(serviceContext1, serviceConfig1, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-issue"));
			SetupGrantService service2 = new SetupGrantService(serviceContext2, serviceConfig2, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-renew"));
			Task<SetupGrantResult> firstTask = service1.IssueAsync("admin", "secret", CancellationToken.None);
			Task<SetupGrantResult> secondTask = service2.RenewAsync("admin", "secret", CancellationToken.None);
			InlineArray2<Task<SetupGrantResult>> buffer = default(InlineArray2<Task<SetupGrantResult>>);
			buffer[0] = firstTask;
			buffer[1] = secondTask;
			SetupGrantResult[] results = await Task.WhenAll<SetupGrantResult>(buffer);
			ConfigManager validatorConfig = new ConfigManager(new ConfigEncryptionService());
			await validatorConfig.LoadConfig();
			SetupGrantService validator = new SetupGrantService(seedContext, validatorConfig, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			bool firstValid = await validator.ValidateAsync(results[0].Grant, CancellationToken.None);
			Assert.True(firstValid ^ await validator.ValidateAsync(results[1].Grant, CancellationToken.None));
			await using DavDatabaseContext verifyContext = await CreateMigratedContextAsync();
			Assert.NotNull(await verifyContext.ConfigItems.SingleOrDefaultAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
			Assert.Single(await verifyContext.Accounts.Where((Account x) => (int)x.Type == 1 && x.Username == "admin").ToListAsync());
			Assert.Single(await verifyContext.SetupGrants.Where((SetupGrant x) => x.Id == 1).ToListAsync());
			ConfigManager persistedConfig = new ConfigManager(new ConfigEncryptionService());
			await persistedConfig.LoadConfig();
			string expectedToken = (firstValid ? "token-issue" : "token-renew");
			Assert.Equal(expectedToken, persistedConfig.GetSetupJellyfinApiKey());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 744)]
	public async Task ConcurrentIssueAndRenewCallsKeepOnlyOneValidGrantAndMatchPersistedToken_Postgres()
	{
		PostgresHeaderCacheFixture fixture = new PostgresHeaderCacheFixture();
		try
		{
			await fixture.InitializeAsync();
			Assert.SkipUnless(fixture.IsAvailable, "Docker is required for PostgreSQL-specific SetupGrant concurrency checks.");
			await fixture.ResetAsync();
			string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-concurrent-postgres-{Guid.NewGuid():N}");
			string connectionString = fixture.ConnectionString;
			using (new TemporaryEnvironment(("DATABASE_URL", connectionString), ("CONFIG_PATH", configPath), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
			{
				Directory.CreateDirectory(configPath);
				await using DavDatabaseContext seedContext = await CreateMigratedContextAsync();
				ConfigManager serviceConfig1 = new ConfigManager(new ConfigEncryptionService());
				ConfigManager serviceConfig2 = new ConfigManager(new ConfigEncryptionService());
				await serviceConfig1.LoadConfig();
				await serviceConfig2.LoadConfig();
				await using DavDatabaseContext serviceContext1 = new DavDatabaseContext();
				await using DavDatabaseContext serviceContext2 = new DavDatabaseContext();
				SetupGrantService service1 = new SetupGrantService(serviceContext1, serviceConfig1, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-issue"));
				SetupGrantService service2 = new SetupGrantService(serviceContext2, serviceConfig2, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-renew"));
				Task<SetupGrantResult> firstTask = service1.IssueAsync("admin", "secret", CancellationToken.None);
				Task<SetupGrantResult> secondTask = service2.RenewAsync("admin", "secret", CancellationToken.None);
				InlineArray2<Task<SetupGrantResult>> buffer = default(InlineArray2<Task<SetupGrantResult>>);
				buffer[0] = firstTask;
				buffer[1] = secondTask;
				SetupGrantResult[] results = await Task.WhenAll<SetupGrantResult>(buffer);
				ConfigManager validatorConfig = new ConfigManager(new ConfigEncryptionService());
				await validatorConfig.LoadConfig();
				SetupGrantService validator = new SetupGrantService(seedContext, validatorConfig, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
				bool firstValid = await validator.ValidateAsync(results[0].Grant, CancellationToken.None);
				Assert.True(firstValid ^ await validator.ValidateAsync(results[1].Grant, CancellationToken.None));
				await using DavDatabaseContext verifyContext = await CreateMigratedContextAsync();
				Assert.NotNull(await verifyContext.ConfigItems.SingleOrDefaultAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
				Assert.Single(await verifyContext.Accounts.Where((Account x) => (int)x.Type == 1 && x.Username == "admin").ToListAsync());
				Assert.Single(await verifyContext.SetupGrants.Where((SetupGrant x) => x.Id == 1).ToListAsync());
				ConfigManager persistedConfig = new ConfigManager(new ConfigEncryptionService());
				await persistedConfig.LoadConfig();
				string expectedToken = (firstValid ? "token-issue" : "token-renew");
				Assert.Equal(expectedToken, persistedConfig.GetSetupJellyfinApiKey());
			}
		}
		finally
		{
			await fixture.DisposeAsync();
		}
	}

	[Fact("SetupGrantServiceTests.cs", 814)]
	public async Task IssueAsync_CanValidateAfterRestartAndReadJellyfinApiKeyViaGetter()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-restart-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			SetupGrantResult issued;
			await using (DavDatabaseContext context = await CreateMigratedContextAsync())
			{
				ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
				await manager.LoadConfig();
				issued = await new SetupGrantService(context, manager, TimeProvider.System, () => CreateJellyfinClient(administrator: true)).IssueAsync("admin", "secret", CancellationToken.None);
			}
			await using DavDatabaseContext restartedContext = await CreateMigratedContextAsync();
			ConfigManager restartedManager = new ConfigManager(new ConfigEncryptionService());
			await restartedManager.LoadConfig();
			SetupGrantService restartedService = new SetupGrantService(restartedContext, restartedManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			Assert.True(await restartedService.ValidateAsync(issued.Grant, CancellationToken.None));
			Assert.Equal("token", restartedManager.GetSetupJellyfinApiKey());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 844)]
	public async Task IssueAsync_CommitsAccountTokenAndGrantAtomically()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-commit-success-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			SetupGrantResult issued;
			await using (DavDatabaseContext context = await CreateMigratedContextAsync())
			{
				ConfigManager manager = new ConfigManager(new ConfigEncryptionService());
				await manager.LoadConfig();
				SetupGrantService service = new SetupGrantService(context, manager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-success"));
				issued = await service.IssueAsync("admin", "secret", CancellationToken.None);
				Account account = await context.Accounts.SingleAsync((Account x) => (int)x.Type == 1 && x.Username == "admin");
				Assert.True(PasswordUtil.Verify(account.PasswordHash, "secret", account.RandomSalt));
				Assert.NotNull(await context.SetupGrants.SingleAsync());
				Assert.NotEqual<string>("token-success", (await context.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key")).ConfigValue);
			}
			await using DavDatabaseContext restartedContext = await CreateMigratedContextAsync();
			ConfigManager restartedManager = new ConfigManager(new ConfigEncryptionService());
			await restartedManager.LoadConfig();
			SetupGrantService restartedService = new SetupGrantService(restartedContext, restartedManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true, "token-success"));
			Assert.True(await restartedService.ValidateAsync(issued.Grant, CancellationToken.None));
			Assert.Equal("token-success", restartedManager.GetSetupJellyfinApiKey());
			DavDatabaseContext verificationContext = await CreateMigratedContextAsync();
			Assert.Single(await verificationContext.Accounts.Where((Account x) => (int)x.Type == 1 && x.Username == "admin").ToListAsync());
			Assert.Single(await verificationContext.SetupGrants.Where((SetupGrant x) => x.Id == 1).ToListAsync());
			await verificationContext.DisposeAsync();
		}
	}

	[Fact("SetupGrantServiceTests.cs", 888)]
	public async Task IssueAsync_PersistenceFailureLeavesDurableCandidateAndRollsBackMainWrites()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-persistence-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			context.ConfigItems.AddRange(new ConfigItem
			{
				ConfigName = "setup.jellyfin-api-key",
				ConfigValue = "legacy",
				IsEncrypted = false
			}, new ConfigItem
			{
				ConfigName = "setup.jellyfin-api-key".ToUpperInvariant(),
				ConfigValue = "legacy-duplicate",
				IsEncrypted = false
			});
			await context.SaveChangesAsync();
			List<(string ConfigName, string ConfigValue, bool IsEncrypted)> legacyRows = (await context.ConfigItems.AsNoTracking().ToListAsync()).Select((ConfigItem row) => (ConfigName: row.ConfigName, ConfigValue: row.ConfigValue, IsEncrypted: row.IsEncrypted)).ToList();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true));
			var issueException = await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueAsync("admin", "secret", CancellationToken.None));
			Console.WriteLine($"IssueAsync exception: {issueException.GetType().Name}: {issueException.Message}");
			Assert.Equal(3, await context.ConfigItems.CountAsync(ci => ci.ConfigName == "setup.candidate-session-token" || ci.ConfigName == "setup.candidate-session-operation" || ci.ConfigName == "setup.candidate-session-intent"));
			Assert.False(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1 && x.Username == "admin"));
			Assert.False(await context.SetupGrants.AnyAsync());
			List<ConfigItem> candidateRows = await context.ConfigItems.Where((ConfigItem x) => x.ConfigName == "setup.candidate-session-token" || x.ConfigName == "setup.candidate-session-operation" || x.ConfigName == "setup.candidate-session-intent").AsNoTracking().ToListAsync();
			Assert.Equal(new string[3] { "setup.candidate-session-intent", "setup.candidate-session-operation", "setup.candidate-session-token" }, from x in candidateRows
				select x.ConfigName into x
				orderby x
				select x);
			Assert.All(candidateRows, delegate(ConfigItem row)
			{
				Assert.True(row.IsEncrypted);
				Assert.True(ConfigEncryptionService.IsEncryptedFormat(row.ConfigValue));
			});
			using ConfigEncryptionService encryption = new ConfigEncryptionService();
			string candidateToken = encryption.Decrypt("setup.candidate-session-token", candidateRows.Single((ConfigItem x) => x.ConfigName == "setup.candidate-session-token").ConfigValue).plaintext;
			string candidateOperation = encryption.Decrypt("setup.candidate-session-operation", candidateRows.Single((ConfigItem x) => x.ConfigName == "setup.candidate-session-operation").ConfigValue).plaintext;
			string candidateIntent = encryption.Decrypt("setup.candidate-session-intent", candidateRows.Single((ConfigItem x) => x.ConfigName == "setup.candidate-session-intent").ConfigValue).plaintext;
			Assert.Equal("token", candidateToken);
			Assert.Equal(candidateOperation, candidateIntent);
			Assert.StartsWith("issue:", candidateOperation, StringComparison.Ordinal);
			Assert.Equal("issue:".Length + 32, candidateOperation.Length);
			Assert.True(Guid.TryParse(candidateOperation.Substring("issue:".Length), out var _));
			List<(string ConfigName, string ConfigValue, bool IsEncrypted)> remainingRows = (from row in await context.ConfigItems.AsNoTracking().ToListAsync()
				where row.ConfigName != "setup.candidate-session-token" && row.ConfigName != "setup.candidate-session-operation" && row.ConfigName != "setup.candidate-session-intent"
				select (ConfigName: row.ConfigName, ConfigValue: row.ConfigValue, IsEncrypted: row.IsEncrypted)).ToList();
			Assert.Equal(from n in legacyRows.Select(row => row.ConfigName).Distinct(StringComparer.OrdinalIgnoreCase)
				orderby n
				select n, from n in remainingRows.Select(row => row.ConfigName).Distinct(StringComparer.OrdinalIgnoreCase)
				orderby n
				select n);
		}
	}

	[Fact("SetupGrantServiceTests.cs", 963)]
	public async Task IssueAsync_CancellationDuringCommitRequiresRecoveryAndPreservesCandidate()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-cancel-after-account-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			CancellationTokenSource cancellationSource = new CancellationTokenSource();
			try
			{
				RevocationCapturingJellyfinHandler handler = new RevocationCapturingJellyfinHandler();
				SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => new HttpClient(handler, disposeHandler: false), null, delegate
				{
					cancellationSource.Cancel();
					return Task.CompletedTask;
				});
				Task<SetupGrantResult> issueTask = service.IssueAsync("admin", "secret", cancellationSource.Token);
				Assert.NotEmpty((await issueTask).Grant);
				Assert.True(cancellationSource.IsCancellationRequested);
				Assert.Equal(0, handler.RevokeCalls);
				Assert.True(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1));
				Assert.Single(await context.SetupGrants.ToListAsync());
				Assert.NotNull(configManager.GetSetupJellyfinApiKey());
				Assert.False(await service.HasRecoveryPendingAsync(CancellationToken.None));
				Assert.False(await context.ConfigItems.AnyAsync((ConfigItem row) => row.ConfigName == "setup.candidate-session-token"));
				Assert.False(await context.ConfigItems.AnyAsync((ConfigItem row) => row.ConfigName == "setup.candidate-session-operation"));
				Assert.False(await context.ConfigItems.AnyAsync((ConfigItem row) => row.ConfigName == "setup.candidate-session-intent"));
			}
			finally
			{
				if (cancellationSource != null)
				{
					((IDisposable)cancellationSource).Dispose();
				}
			}
		}
	}

	[Fact("SetupGrantServiceTests.cs", 1008)]
	public async Task IssueAsync_CommitFailureDoesNotPublishSetupCache()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-commit-failure-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext context = await CreateMigratedContextAsync();
			ConfigManager configManager = new ConfigManager(new ConfigEncryptionService());
			await configManager.LoadConfig();
			bool published = false;
			configManager.OnConfigChanged += delegate
			{
				published = true;
			};
			SetupGrantService service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(administrator: true), null, (IDbContextTransaction _, CancellationToken _) => Task.FromException(new InvalidOperationException("commit failed")));
			await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueAsync("admin", "secret", CancellationToken.None));
			Assert.False(published);
			Assert.False(await context.Accounts.AnyAsync((Account x) => (int)x.Type == 1 && x.Username == "admin"));
			Assert.False(await context.SetupGrants.AnyAsync());
			Assert.False(await context.ConfigItems.AnyAsync((ConfigItem x) => x.ConfigName == "setup.jellyfin-api-key"));
			Assert.Null(configManager.GetSetupJellyfinApiKey());
		}
	}

	[Fact("SetupGrantServiceTests.cs", 1044)]
	public async Task IssueAsync_DetectsConcurrentCompletionBeforePersistingGrant_SQLite()
	{
		string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-completion-race-sqlite-{Guid.NewGuid():N}");
		using (new TemporaryEnvironment(("CONFIG_PATH", configPath), ("DATABASE_URL", null), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
		{
			Directory.CreateDirectory(configPath);
			await using DavDatabaseContext seededContext = await CreateMigratedContextAsync();
			seededContext.SetupGrants.Add(new SetupGrant
			{
				Id = 1,
				GrantedTokenHash = SetupGrantCrypto.ComputeIssuedTokenHash("revoked-token"),
				IssuedAtUtc = DateTime.UtcNow.AddMinutes(-10.0),
				ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5.0),
				IsRevoked = true,
				RevokedAtUtc = DateTime.UtcNow.AddMinutes(-6.0),
				IssuedByUsername = "admin"
			});
			await seededContext.SaveChangesAsync();
			TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource allowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			ConfigManager serviceManager = new ConfigManager(new ConfigEncryptionService());
			await serviceManager.LoadConfig();
			await using DavDatabaseContext serviceContext = await CreateMigratedContextAsync();
			SetupGrantService service = new SetupGrantService(serviceContext, serviceManager, TimeProvider.System, () => CreateLatchingJellyfinClient((int _) => (Administrator: true, Token: "token-new"), started, allowed));
			Task<SetupGrantResult> issueTask = service.IssueAsync("admin", "secret", CancellationToken.None);
			await started.Task;
			await using (DavDatabaseContext completionContext = await CreateMigratedContextAsync())
			{
				ConfigItem completed = await completionContext.ConfigItems.SingleOrDefaultAsync((ConfigItem x) => x.ConfigName == "setup.completed");
				if (completed == null)
				{
					completionContext.ConfigItems.Add(new ConfigItem
					{
						ConfigName = "setup.completed",
						ConfigValue = "true",
						IsEncrypted = false
					});
				}
				else
				{
					completed.ConfigValue = "true";
				}
				await completionContext.SaveChangesAsync();
			}
			allowed.TrySetResult();
			await Assert.ThrowsAsync<BadHttpRequestException>(() => issueTask);
			await using DavDatabaseContext verifyContext = await CreateMigratedContextAsync();
			SetupGrant persistedGrant = await verifyContext.SetupGrants.SingleAsync();
			Assert.True(persistedGrant.IsRevoked);
			Assert.Equal(SetupGrantCrypto.ComputeIssuedTokenHash("revoked-token"), persistedGrant.GrantedTokenHash);
			Assert.Equal("true", (await verifyContext.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.completed")).ConfigValue);
		}
	}

	[Fact("SetupGrantServiceTests.cs", 1121)]
	public async Task IssueAsync_DetectsConcurrentCompletionBeforePersistingGrant_Postgres()
	{
		PostgresHeaderCacheFixture fixture = new PostgresHeaderCacheFixture();
		try
		{
			await fixture.InitializeAsync();
			Assert.SkipUnless(fixture.IsAvailable, "Docker is required for PostgreSQL-specific race checks.");
			await fixture.ResetAsync();
			string configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-completion-race-postgres-{Guid.NewGuid():N}");
			string connectionString = fixture.ConnectionString;
			using (new TemporaryEnvironment(("DATABASE_URL", connectionString), ("CONFIG_PATH", configPath), ("NZBDAV_FULL_STACK", "true"), ("SETUP_JELLYFIN_URL", "http://jellyfin.test"), ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))))
			{
				Directory.CreateDirectory(configPath);
				await using DavDatabaseContext seededContext = await CreateMigratedContextAsync();
				seededContext.SetupGrants.Add(new SetupGrant
				{
					Id = 1,
					GrantedTokenHash = SetupGrantCrypto.ComputeIssuedTokenHash("revoked-token"),
					IssuedAtUtc = DateTime.UtcNow.AddMinutes(-10.0),
					ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5.0),
					IsRevoked = true,
					RevokedAtUtc = DateTime.UtcNow.AddMinutes(-6.0),
					IssuedByUsername = "admin"
				});
				await seededContext.SaveChangesAsync();
				TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				TaskCompletionSource allowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				ConfigManager serviceManager = new ConfigManager(new ConfigEncryptionService());
				await serviceManager.LoadConfig();
				await using DavDatabaseContext serviceContext = await CreateMigratedContextAsync();
				SetupGrantService service = new SetupGrantService(serviceContext, serviceManager, TimeProvider.System, () => CreateLatchingJellyfinClient((int _) => (Administrator: true, Token: "token-new"), started, allowed));
				Task<SetupGrantResult> issueTask = service.IssueAsync("admin", "secret", CancellationToken.None);
				await started.Task;
				await using DavDatabaseContext completionContext = await CreateMigratedContextAsync();
				ConfigItem completed = await completionContext.ConfigItems.SingleOrDefaultAsync((ConfigItem x) => x.ConfigName == "setup.completed");
				if (completed == null)
				{
					completionContext.ConfigItems.Add(new ConfigItem
					{
						ConfigName = "setup.completed",
						ConfigValue = "true",
						IsEncrypted = false
					});
				}
				else
				{
					completed.ConfigValue = "true";
				}
				await completionContext.SaveChangesAsync();
				allowed.TrySetResult();
				await Assert.ThrowsAsync<BadHttpRequestException>(() => issueTask);
				await using DavDatabaseContext verifyContext = await CreateMigratedContextAsync();
				SetupGrant persistedGrant = await verifyContext.SetupGrants.SingleAsync();
				Assert.True(persistedGrant.IsRevoked);
				Assert.Equal(SetupGrantCrypto.ComputeIssuedTokenHash("revoked-token"), persistedGrant.GrantedTokenHash);
				Assert.Equal("true", (await verifyContext.ConfigItems.SingleAsync((ConfigItem x) => x.ConfigName == "setup.completed")).ConfigValue);
			}
		}
		finally
		{
			await fixture.DisposeAsync();
		}
	}

	private static HttpClient CreateJellyfinClient(Func<int, (bool Administrator, string Token)> responseFactory, TimeSpan? delay = null)
	{
		JellyfinAuthenticationHandler handler = new JellyfinAuthenticationHandler(responseFactory, delay);
		return new HttpClient(handler, disposeHandler: true);
	}

	private static HttpClient CreateJellyfinClient(bool administrator, string token = "token", TimeSpan? delay = null)
	{
		return CreateJellyfinClient((int _) => (Administrator: administrator, Token: token), delay);
	}

	private static HttpClient CreateLatchingJellyfinClient(Func<int, (bool Administrator, string Token)> responseFactory, TaskCompletionSource authStarted, TaskCompletionSource authAllowed, TimeSpan? delay = null)
	{
		LatchingJellyfinAuthenticationHandler handler = new LatchingJellyfinAuthenticationHandler(responseFactory, authStarted, authAllowed, delay);
		return new HttpClient(handler, disposeHandler: true);
	}

	private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
	{
		DavDatabaseContext context = new DavDatabaseContext();
		if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DATABASE_URL")))
		{
			await context.Database.MigrateAsync();
		}
		return context;
	}
}

﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans.Configuration;
using Orleans.Persistence.Redis.Compression;
using Orleans.Persistence.Redis.Config;
using Orleans.Persistence.Redis.Core;
using Orleans.Persistence.Redis.Serialization;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using System;
using JsonSerializer = Orleans.Persistence.Redis.Serialization.JsonSerializer;
using OrleansSerializer = Orleans.Persistence.Redis.Serialization.OrleansSerializer;

// ReSharper disable once CheckNamespace
namespace Orleans.Hosting;

public static class RedisSiloBuilderExtensions
{
	public static RedisStorageOptionsBuilder AddRedisGrainStorage(
		this ISiloBuilder builder,
		string name
	) => new RedisStorageOptionsBuilder(builder, name);

	public static RedisStorageOptionsBuilder AddRedisGrainStorageAsDefault(
		this ISiloBuilder builder
	) => builder.AddRedisGrainStorage("Default");
		
	internal static IServiceCollection AddRedisGrainStorage(
		this IServiceCollection services,
		string name,
		Action<OptionsBuilder<RedisStorageOptions>> configureOptions = null
	)
	{
		configureOptions?.Invoke(services.AddOptions<RedisStorageOptions>(name));
		// services.AddTransient<IConfigurationValidator>(sp => new DynamoDBGrainStorageOptionsValidator(sp.GetService<IOptionsSnapshot<RedisStorageOptions>>().Get(name), name));
		services.AddSingletonNamedService(name, CreateStateStore);
		services.ConfigureNamedOptionForLogging<RedisStorageOptions>(name);
		services.TryAddSingleton(sp =>
			sp.GetServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));

		return services
			.AddSingletonNamedService(name, CreateDbConnection)
			.AddSingletonNamedService(name, CreateRedisStorage)
			.AddSingletonNamedService(name, (provider, n)
				=> (ILifecycleParticipant<ISiloLifecycle>)provider.GetRequiredServiceByName<IGrainStorage>(n));
	}

	internal static ISiloBuilder AddRedisDefaultSerializer(this ISiloBuilder builder, string name)
		=> builder.AddRedisSerializer<OrleansSerializer>(name);

	internal static ISiloBuilder AddRedisDefaultBrotliSerializer(this ISiloBuilder builder, string name)
		=> builder.AddRedisSerializer<BrotliSerializer>(name);
		 
	internal static ISiloBuilder AddRedisDefaultHumanReadableSerializer(this ISiloBuilder builder, string name)
		=> builder.AddRedisHumanReadableSerializer<JsonSerializer>(
			name,
			provider => new object[] { RedisDefaultJsonSerializerSettings.Get(provider) }
		);

	internal static ISiloBuilder AddCompression<TCompression>(this ISiloBuilder builder, string name)
		where TCompression : ICompression
		=> builder.ConfigureServices(services =>
			services.AddSingletonNamedService<ICompression>(name, (provider, n)
				=> ActivatorUtilities.CreateInstance<TCompression>(provider)));

	internal static ISiloBuilder AddRedisSerializer<TSerializer>(
		this ISiloBuilder builder,
		string name,
		params object[] settings
	)
		where TSerializer : ISerializer
		=> builder.ConfigureServices(services =>
			services.AddSingletonNamedService<ISerializer>(name, (provider, n)
				=> ActivatorUtilities.CreateInstance<TSerializer>(provider, settings))
		);

	internal static ISiloBuilder AddRedisHumanReadableSerializer<TSerializer>(
		this ISiloBuilder builder,
		string name,
		params object[] settings
	)
		where TSerializer : IHumanReadableSerializer
		=> builder.ConfigureServices(services =>
			services.AddSingletonNamedService<IHumanReadableSerializer>(name, (provider, n)
				=> ActivatorUtilities.CreateInstance<TSerializer>(provider, settings))
		);

	internal static ISiloBuilder AddRedisHumanReadableSerializer<TSerializer>(
		this ISiloBuilder builder,
		string name,
		Func<IServiceProvider, object[]> cfg
	) where TSerializer : IHumanReadableSerializer
		=> builder.ConfigureServices(services =>
			services.AddSingletonNamedService<IHumanReadableSerializer>(name, (provider, n)
				=> ActivatorUtilities.CreateInstance<TSerializer>(provider, cfg?.Invoke(provider)))
		);
		
	private static IGrainStorage CreateRedisStorage(IServiceProvider services, string name)
	{
		var store = services.GetRequiredServiceByName<IGrainStateStore>(name);
		var connection = services.GetRequiredServiceByName<DbConnection>(name);
		return ActivatorUtilities.CreateInstance<RedisGrainStorage>(services, name, store, connection);
	}

	private static IGrainStateStore CreateStateStore(IServiceProvider provider, string name)
	{
		var connection = provider.GetRequiredServiceByName<DbConnection>(name);
		var serializer = provider.GetRequiredServiceByName<ISerializer>(name);
		var humanReadableSerializer = provider.GetServiceByName<IHumanReadableSerializer>(name);
		var options = provider.GetRequiredService<IOptionsSnapshot<RedisStorageOptions>>();
		var compress = provider.GetServiceByName<ICompression>(name);

		if (compress != null)
			return ActivatorUtilities.CreateInstance<GrainStateStore>(
				provider,
				connection,
				options.Get(name),
				serializer,
				humanReadableSerializer,
				compress
			);

		return ActivatorUtilities.CreateInstance<GrainStateStore>(
			provider,
			connection,
			options.Get(name),
			serializer,
			humanReadableSerializer
		);
	}

	private static DbConnection CreateDbConnection(IServiceProvider provider, string name)
	{
		var optionsSnapshot = provider.GetRequiredService<IOptionsSnapshot<RedisStorageOptions>>();
		var logger = provider.GetRequiredService<ILogger<DbConnection>>();
		return ActivatorUtilities.CreateInstance<DbConnection>(provider, optionsSnapshot.Get(name), logger);
	}
}

public static class RedisDefaultJsonSerializerSettings
{
	public static JsonSerializerSettings Get(IServiceProvider provider)
	{
		var settings = OrleansJsonSerializerSettings.GetDefaultSerializerSettings(provider);

		settings.ContractResolver = new DefaultContractResolver
		{
			NamingStrategy = new DefaultNamingStrategy
			{
				ProcessDictionaryKeys = false
			}
		};

		return settings;
	}
}
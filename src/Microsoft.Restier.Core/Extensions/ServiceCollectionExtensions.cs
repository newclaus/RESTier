﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Restier.Core.Operation;
using Microsoft.Restier.Core.Query;
using Microsoft.Restier.Core.Submit;

namespace Microsoft.Restier.Core
{
    /// <summary>
    /// A delegate which participate in service creation.
    /// All registered contributors form a chain, and the last registered will be called first.
    /// </summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <param name="serviceProvider">The <see cref="IServiceProvider"/> to which this contributor call is registered.</param>
    /// <param name="next">Return the result of the previous contributor on the chain.</param>
    /// <returns>A service instance of <typeparamref name="T"/>.</returns>
    internal delegate T ApiServiceContributor<T>(IServiceProvider serviceProvider, Func<T> next) where T : class;

    /// <summary>
    /// Contains extension methods of <see cref="IServiceCollection"/>.
    /// </summary>
    public static class ServiceCollectionExtensions
    {

        /// <summary>
        /// Return true if the <see cref="IServiceCollection"/> has any <typeparamref name="TService"/> service registered.
        /// </summary>
        /// <typeparam name="TService">The service type to register with the <see cref="IServiceCollection"/>.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to register the <typeparamref name="TService"/> with.</param>
        /// <returns>
        /// A <see cref="bool"/> specifying whether or not the <typeparamref name="TService"/>
        /// </returns>
        public static bool HasService<TService>(this IServiceCollection services) where TService : class
        {
            Ensure.NotNull(services, nameof(services));

            return services.Any(sd => sd.ServiceType == typeof(TService));
        }

        /// <summary>
        /// Returns the number of services that match the given <see cref="ServiceDescriptor.ServiceType"/> in a given <see cref="ServiceCollection"/>.
        /// </summary>
        /// <typeparam name="TService">The service type to register with the <see cref="IServiceCollection"/>.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to register the <typeparamref name="TService"/> with.</param>
        /// <returns>
        /// An <see cref="int"/> representing the number of Services that match the given ServiceType.
        /// </returns>
        public static int HasServiceCount<TService>(this IServiceCollection services) where TService : class
        {
            Ensure.NotNull(services, nameof(services));

            return services.Count(sd => sd.ServiceType == typeof(TService));
        }

        /// <summary>
        /// A Restier-specific method that adds a "service contributor", which has a chance to chain previously registered service instances. 
        /// DO NOT use this method outside of a Restier app. 
        /// </summary>
        /// <typeparam name="TService">The service type to register with the <see cref="IServiceCollection"/>.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to register the <typeparamref name="TService"/> with.</param>
        /// <param name="factory">A factory method to create a new instance of service TService, wrapping previous instance."/>.</param>
        /// <param name="serviceLifetime">The <see cref="ServiceLifetime"/> of the service being added.</param>
        /// <returns>
        /// The <paramref name="services"/> instance modified with the new <typeparamref name="TService"/> reference.
        /// </returns>
        /// <remarks>
        /// This process is being deprecated. Please DO NOT rely on it for future behavior in your own apps. V2 will properly handle 
        /// multiple instances of a registration by firing them in succession.
        /// </remarks>
        public static IServiceCollection AddChainedService<TService>(
            this IServiceCollection services,
            Func<IServiceProvider, TService, TService> factory,
            ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
            where TService : class
        {
            Ensure.NotNull(services, nameof(services));
            Ensure.NotNull(factory, nameof(factory));
            return services.AddContributorNoCheck<TService>((sp, next) => factory(sp, next()), serviceLifetime);
        }

        /// <summary>
        /// A Restier-specific method that adds a "service contributor", which has a chance to chain previously registered service instances. 
        /// DO NOT use this method outside of a Restier app. 
        /// </summary>
        /// <remarks>
        /// <para>
        /// This process is being deprecated. Please DO NOT rely on it for future behavior in your own apps. V2 will properly handle 
        /// multiple instances of a registration by firing them in succession.
        /// </para>
        /// <para>
        /// If want to cutoff previous registration, not define a property with type of TService or do not use it.
        /// The contributor added will get an instance of <typeparamref name="TImplement"/> from the container, i.e.
        /// <see cref="IServiceProvider"/>, every time it's get called.
        /// This method will try to register <typeparamref name="TImplement"/> as a service with
        /// <see cref="ServiceLifetime.Transient"/> life time, if it's not yet registered. To override, you can
        /// register <typeparamref name="TImplement"/> before or after calling this method.
        /// </para>
        /// <para>
        /// Note: When registering <typeparamref name="TImplement"/>, you must NOT give it a
        /// <see cref="ServiceLifetime"/> that makes it outlives <typeparamref name="TService"/>, that could possibly
        /// make an instance of <typeparamref name="TImplement"/> be used in multiple instantiations of
        /// <typeparamref name="TService"/>, which leads to unpredictable behaviors.
        /// </para>
        /// </remarks>
        /// <typeparam name="TService">The service type to register with the <see cref="IServiceCollection"/>.</typeparam>
        /// <typeparam name="TImplement">The implementation type.</typeparam>
        /// <param name="services">The <see cref="IServiceCollection"/> to register the <typeparamref name="TService"/> with.</param>
        /// <param name="serviceLifetime">The <see cref="ServiceLifetime"/> of the service being added.</param>
        /// <returns>
        /// Current <see cref="IServiceCollection"/>
        /// </returns>
        public static IServiceCollection AddChainedService<TService, TImplement>(this IServiceCollection services, ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
            where TService : class
            where TImplement : class, TService
        {
            Ensure.NotNull(services, nameof(services));

            Func<IServiceProvider, Func<TService>, TService> factory = null;

            services.TryAddTransient<TImplement>();
            return services.AddContributorNoCheck<TService>((sp, next) =>
            {
                if (factory != null)
                {
                    return factory(sp, next);
                }

                var instance = sp.GetService<TImplement>();
                if (instance == null)
                {
                    return instance;
                }

                var innerMember = FindInnerMemberAndInject(instance, next);
                if (innerMember == null)
                {
                    factory = (serviceProvider, _) => serviceProvider.GetRequiredService<TImplement>();
                    return instance;
                }

                factory = (serviceProvider, getNext) =>
                {
                    // To build a lambda expression like:
                    // (sp, next) =>
                    // {
                    //     var service = sp.GetRequiredService<TImplement>();
                    //     service.next = next();
                    //     return service;
                    // }
                    var serviceProviderParam = Expression.Parameter(typeof(IServiceProvider));
                    var nextParam = Expression.Parameter(typeof(Func<TService>));

                    var value = Expression.Variable(typeof(TImplement));
                    var getService = Expression.Call(
                        typeof(ServiceProviderServiceExtensions),
                        "GetRequiredService",
                        new[] { typeof(TImplement) },
                        serviceProviderParam);
                    var inject = Expression.Assign(
                        Expression.MakeMemberAccess(value, innerMember),
                        Expression.Invoke(nextParam));

                    var block = Expression.Block(
                        typeof(TService),
                        new[] { value },
                        Expression.Assign(value, getService),
                        inject,
                        value);

                    factory = LambdaExpression.Lambda<Func<IServiceProvider, Func<TService>, TService>>(
                        block,
                        serviceProviderParam,
                        nextParam).Compile();
                    innerMember = null;
                    return factory(serviceProvider, getNext);
                };

                return instance;
            }, serviceLifetime);
        }

        /// <summary>
        /// Add core services.
        /// </summary>
        /// <param name="services">he <see cref="IServiceCollection"/> containing API service registrations.</param>
        /// <param name="apiType">The type of a class on which code-based conventions are used.</param>
        /// <returns>
        /// Current <see cref="IServiceCollection"/>
        /// </returns>
        public static IServiceCollection AddRestierCoreServices(this IServiceCollection services, Type apiType)
        {
            Ensure.NotNull(services, nameof(services));
            Ensure.NotNull(apiType, nameof(apiType));

            services.AddScoped(apiType, apiType)
                .AddScoped(typeof(ApiBase), apiType);

            services.TryAddSingleton<ApiConfiguration>();

            return services.AddChainedService<IQueryExecutor, DefaultQueryExecutor>()
                            .AddScoped<PropertyBag>();
        }

        /// <summary>
        /// Enables code-based conventions for an API.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> containing API service registrations.</param>
        /// <param name="apiType">The type of a class on which code-based conventions are used.</param>
        /// <returns>Current <see cref="IServiceCollection"/></returns>
        public static IServiceCollection AddRestierConventionBasedServices(this IServiceCollection services, Type apiType)
        {
            Ensure.NotNull(services, nameof(services));
            Ensure.NotNull(apiType, nameof(apiType));

            services.AddChainedService<IChangeSetItemAuthorizer>((sp, next) => new ConventionBasedChangeSetItemAuthorizer(apiType));
            services.AddChainedService<IChangeSetItemFilter>((sp, next) => new ConventionBasedChangeSetItemFilter(apiType));
            services.AddChainedService<IChangeSetItemValidator, ConventionBasedChangeSetItemValidator>();
            services.AddChainedService<IQueryExpressionProcessor>((sp, next) => new ConventionBasedQueryExpressionProcessor(apiType)
            {
                Inner = next,
            });
            services.AddChainedService<IOperationAuthorizer>((sp, next) => new ConventionBasedOperationAuthorizer(apiType));
            services.AddChainedService<IOperationFilter>((sp, next) => new ConventionBasedOperationFilter(apiType));
            return services;
        }

        private static IServiceCollection AddContributorNoCheck<TService>(
            this IServiceCollection services,
            ApiServiceContributor<TService> contributor,
            ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
            where TService : class
        {
            var serviceDescriptor = new ServiceDescriptor(typeof(TService), ChainedService<TService>.DefaultFactory, serviceLifetime);

            services.TryAdd(serviceDescriptor);
            services.AddSingleton(contributor);

            return services;
        }

        private static MemberInfo FindInnerMemberAndInject<TService, TImplement>(TImplement instance, Func<TService> next)
        {
            var typeInfo = typeof(TImplement).GetTypeInfo();
            var nextProperty = typeInfo
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(e => e.SetMethod != null && e.PropertyType == typeof(TService));
            if (nextProperty != null)
            {
                nextProperty.SetValue(instance, next());
                return nextProperty;
            }

            var nextField = typeInfo
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(e => e.FieldType == typeof(TService));
            if (nextField != null)
            {
                nextField.SetValue(instance, next());
                return nextField;
            }

            return null;
        }
    }
}

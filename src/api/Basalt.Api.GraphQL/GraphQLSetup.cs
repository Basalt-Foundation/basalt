using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Api.GraphQL;

public static class GraphQLSetup
{
    public static IServiceCollection AddBasaltGraphQL(this IServiceCollection services)
    {
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddMaxExecutionDepthRule(10)
            .ModifyRequestOptions(opt => opt.ExecutionTimeout = TimeSpan.FromSeconds(10));

        return services;
    }

    public static IEndpointRouteBuilder MapBasaltGraphQL(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGraphQL("/graphql");
        return endpoints;
    }
}

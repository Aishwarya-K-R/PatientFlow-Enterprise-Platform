using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Patient_Management_System.Models;
using Xunit;

public class PatientTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task GetPatients_Should_Return_401_When_No_Token()
    {
        var response = await _client.GetAsync("/api/patients");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPatients_Should_Return_200_With_Valid_Token()
    {
        var loginResponse = await _client.PostAsJsonAsync("/auth/login", new
        {
            email = "user-1@gmail.com",
            password = "PMS"
        });

        // Parse the typed JSON payload instead of regexing a string.
        var payload = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        payload.Should().NotBeNull("login should return a LoginResponse JSON body");
        var token = payload!.AccessToken;

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.GetAsync("/api/patients");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
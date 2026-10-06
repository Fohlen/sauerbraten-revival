using System;
using System.Collections.Generic;

namespace SauerbratenRevival.Tests;

/// <summary>
/// Tiny assertion helper so tests have no extra dependencies beyond GoDotTest.
/// GoDotTest is a runner only; it does not ship an assertion API.
/// </summary>
public static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception($"Assertion failed: {message}");
    }

    public static void False(bool condition, string message)
    {
        if (condition) throw new Exception($"Assertion failed: {message}");
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Assertion failed: {message} (expected {expected}, got {actual})");
    }
}

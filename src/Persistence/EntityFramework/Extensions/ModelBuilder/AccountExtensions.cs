// <copyright file="AccountExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Extensions.ModelBuilder;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// Extensions for the <see cref="EntityTypeBuilder{Account}"/>.
/// </summary>
internal static class AccountExtensions
{
    /// <summary>
    /// Applies the settings for the <see cref="Account"/> entity.
    /// </summary>
    /// <param name="builder">The builder.</param>
    public static void Apply(this EntityTypeBuilder<Account> builder)
    {
        builder.Property(account => account.LoginName).HasMaxLength(10).IsRequired();
        builder.HasIndex(account => account.LoginName).IsUnique();

        builder.Property(account => account.GoogleSubject).HasMaxLength(255);
        builder.HasIndex(account => account.GoogleSubject).IsUnique();
        builder.Property(account => account.BrowserSessionTokenHash).HasMaxLength(64);
        builder.HasIndex(account => account.BrowserSessionTokenHash).IsUnique();
        builder.Property(account => account.BrowserLoginTicketHash).HasMaxLength(64);
        builder.HasIndex(account => account.BrowserLoginTicketHash).IsUnique();

        builder.Property(account => account.LanguageIsoCode).HasMaxLength(3).IsRequired().HasDefaultValue("en");
    }
}

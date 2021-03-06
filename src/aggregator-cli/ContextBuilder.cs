﻿using Microsoft.Azure.Management.Fluent;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace aggregator.cli
{

    internal class CommandContext
    {
        internal ILogger Logger { get; private set; }
        internal IAzure Azure { get; private set; }
        internal VssConnection Vsts { get; private set; }
        internal CommandContext(ILogger logger, IAzure azure, VssConnection vsts)
        {
            Logger = logger;
            Azure = azure;
            Vsts = vsts;
        }
    }

    internal class ContextBuilder
    {
        ILogger logger;
        bool azureLogon = false;
        bool vstsLogon = false;

        internal ContextBuilder(ILogger logger) => this.logger = logger;

        internal ContextBuilder WithAzureLogon()
        {
            azureLogon = true;
            return this;
        }
        internal ContextBuilder WithVstsLogon()
        {
            vstsLogon = true;
            return this;
        }
        internal async Task<CommandContext> Build()
        {
            IAzure azure = null;
            VssConnection vsts = null;

            if (azureLogon)
            {
                logger.WriteInfo($"Authenticating to Azure...");
                var (connection, reason) = AzureLogon.Load();
                if (reason != LogonResult.Succeeded)
                {
                    string msg = TranslateResult(reason);
                    throw new ApplicationException(string.Format(msg, "Azure","logon.azure"));
                }
                azure = await connection.LogonAsync();
            }

            if (vstsLogon)
            {
                logger.WriteInfo($"Authenticating to VSTS...");
                var (connection, reason) = VstsLogon.Load();
                if (reason != LogonResult.Succeeded)
                {
                    string msg = TranslateResult(reason);
                    throw new ApplicationException(string.Format(msg, "VSTS", "logon.vsts"));
                }
                vsts = await connection.LogonAsync();
            }

            return new CommandContext(logger, azure, vsts);
        }

        private string TranslateResult(LogonResult reason)
        {
            switch (reason)
            {
                case LogonResult.Succeeded:
                    // this should never happen!!!
                    return "Valid credential, logon succeeded";
                case LogonResult.NoLogonData:
                    return "No cached {0} credential: use the {1} command.";
                case LogonResult.LogonExpired:
                    return "Cached {0} credential expired: use the {1} command.";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
    }
}

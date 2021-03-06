﻿using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace aggregator.cli
{
    [Verb("uninstall.instance", HelpText = "Destroy an Aggregator instance in Azure.")]
    class UninstallInstanceCommand : CommandBase
    {
        [Option('n', "name", Required = true, HelpText = "Aggregator instance name.")]
        public string Name { get; set; }

        [Option('l', "location", Required = true, HelpText = "Aggregator instance location (Azure region).")]
        public string Location { get; set; }

        [Option('m', "dont-remove-mappings", Required = false, HelpText = "Do not remove mappings from VSTS (default is to remove them).")]
        public bool Mappings { get; set; }

        internal override async Task<int> RunAsync()
        {
            var context = await Context
                .WithAzureLogon()
                .WithVstsLogon()
                .Build();

            var instance = new InstanceName(Name);

            bool ok;
            if (!Mappings)
            {
                var mappings = new AggregatorMappings(context.Vsts, context.Azure, context.Logger);
                ok = await mappings.RemoveInstanceAsync(instance);
            }

            var instances = new AggregatorInstances(context.Azure, context.Logger);
            ok = await instances.Remove(instance, Location);
            return ok ? 0 : 1;
        }
    }
}

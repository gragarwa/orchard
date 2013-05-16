﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using JetBrains.Annotations;
using Orchard.ContentManagement;
using Orchard.ContentManagement.MetaData;
using Orchard.Environment.Descriptor;
using Orchard.FileSystems.AppData;
using Orchard.ImportExport.Models;
using Orchard.Localization;
using Orchard.Logging;
using Orchard.Recipes.Services;
using VersionOptions = Orchard.ContentManagement.VersionOptions;

namespace Orchard.ImportExport.Services {
    [UsedImplicitly]
    public class ImportExportService : IImportExportService {
        private readonly IOrchardServices _orchardServices;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IContentDefinitionWriter _contentDefinitionWriter;
        private readonly IAppDataFolder _appDataFolder;
        private readonly IRecipeParser _recipeParser;
        private readonly IRecipeManager _recipeManager;
        private readonly IShellDescriptorManager _shellDescriptorManager;
        private readonly IEnumerable<IExportEventHandler> _exportEventHandlers;
        private const string ExportsDirectory = "Exports";

        public ImportExportService(
            IOrchardServices orchardServices,
            IContentDefinitionManager contentDefinitionManager,
            IContentDefinitionWriter contentDefinitionWriter,
            IAppDataFolder appDataFolder,
            IRecipeParser recipeParser, 
            IRecipeManager recipeManager, 
            IShellDescriptorManager shellDescriptorManager,
            IEnumerable<IExportEventHandler> exportEventHandlers) {
            _orchardServices = orchardServices;
            _contentDefinitionManager = contentDefinitionManager;
            _contentDefinitionWriter = contentDefinitionWriter;
            _appDataFolder = appDataFolder;
            _recipeParser = recipeParser;
            _recipeManager = recipeManager;
            _shellDescriptorManager = shellDescriptorManager;
            _exportEventHandlers = exportEventHandlers;
            Logger = NullLogger.Instance;
            T = NullLocalizer.Instance;
        }

        public Localizer T { get; set; }
        public ILogger Logger { get; set; }

        public void Import(string recipeText) {
            var recipe = _recipeParser.ParseRecipe(recipeText);
            _recipeManager.Execute(recipe);
            UpdateShell();
        }

        public string Export(IEnumerable<string> contentTypes, ExportOptions exportOptions) {
            var exportDocument = CreateExportRoot();

            var context = new ExportContext {
                Document = exportDocument,
                ContentTypes = contentTypes,
                ExportOptions = exportOptions
            };

            _exportEventHandlers.Invoke(x => x.Exporting(context), Logger);

            if (exportOptions.ExportMetadata) {
                exportDocument.Element("Orchard").Add(ExportMetadata(contentTypes));
            }

            if (exportOptions.ExportSiteSettings) {
                exportDocument.Element("Orchard").Add(ExportSiteSettings());
            }

            if (exportOptions.ExportData) {
                exportDocument.Element("Orchard").Add(ExportData(contentTypes, exportOptions.VersionHistoryOptions));
            }

            _exportEventHandlers.Invoke(x => x.Exported(context), Logger);

            return WriteExportFile(exportDocument.ToString());
        }

        private XDocument CreateExportRoot() {
            var exportRoot = new XDocument(
                new XDeclaration("1.0", "", "yes"),
                new XComment("Exported from Orchard"),
                new XElement("Orchard",
                             new XElement("Recipe",
                                          new XElement("Name", "Generated by Orchard.ImportExport"),
                                          new XElement("Author", _orchardServices.WorkContext.CurrentUser.UserName)
                                 )
                    )
                );
            return exportRoot;
        }

        private XElement ExportMetadata(IEnumerable<string> contentTypes) {
            var typesElement = new XElement("Types");
            var partsElement = new XElement("Parts");
            var typesToExport = _contentDefinitionManager.ListTypeDefinitions()
                .Where(typeDefinition => contentTypes.Contains(typeDefinition.Name))
                .ToList();
            var partsToExport = new List<string>();

            foreach (var contentTypeDefinition in typesToExport) {
                foreach (var contentPartDefinition in contentTypeDefinition.Parts) {
                    if (partsToExport.Contains(contentPartDefinition.PartDefinition.Name)) {
                        continue;
                    }
                    partsToExport.Add(contentPartDefinition.PartDefinition.Name);
                    partsElement.Add(_contentDefinitionWriter.Export(contentPartDefinition.PartDefinition));
                }
                typesElement.Add(_contentDefinitionWriter.Export(contentTypeDefinition));
            }

            return new XElement("Metadata", typesElement, partsElement);
        }

        private XElement ExportSiteSettings() {
            var settings = new XElement("Settings");
            var hasSetting = false;

            foreach (var sitePart in _orchardServices.WorkContext.CurrentSite.ContentItem.Parts) {
                var setting = new XElement(sitePart.PartDefinition.Name);

                foreach (var property in sitePart.GetType().GetProperties()) {
                    var propertyType = property.PropertyType;
                    // Supported types (we also know they are not indexed properties).
                    if (propertyType == typeof(string) || propertyType == typeof(bool) || propertyType == typeof(int)) {
                        // Exclude read-only properties.
                        if (property.GetSetMethod() != null) {
                            setting.SetAttributeValue(property.Name, property.GetValue(sitePart, null));
                            hasSetting = true;
                        }
                    }
                }

                if (hasSetting) {
                    settings.Add(setting);
                    hasSetting = false;
                }
            }

            return settings;
        }

        private XElement ExportData(IEnumerable<string> contentTypes, VersionHistoryOptions versionHistoryOptions) {
            var data = new XElement("Data");
            var options = GetContentExportVersionOptions(versionHistoryOptions);

            var contentItems = _orchardServices.ContentManager.Query(options).List();

            foreach (var contentType in contentTypes) {
                var type = contentType;
                var items = contentItems.Where(i => i.ContentType == type);
                foreach (var contentItem in items) {
                    var contentItemElement = ExportContentItem(contentItem);
                    if (contentItemElement != null) 
                        data.Add(contentItemElement);
                }
            }

            return data;
        }

        private XElement ExportContentItem(ContentItem contentItem) {
            // Call export handler for the item.
            return _orchardServices.ContentManager.Export(contentItem);
        }

        private static VersionOptions GetContentExportVersionOptions(VersionHistoryOptions versionHistoryOptions) {
            if (versionHistoryOptions.HasFlag(VersionHistoryOptions.Draft)) {
                return VersionOptions.Draft;
            }
            return VersionOptions.Published;
        }

        private string WriteExportFile(string exportDocument) {
            var exportFile = string.Format("Export-{0}-{1}.xml", _orchardServices.WorkContext.CurrentUser.UserName, DateTime.UtcNow.Ticks);
            if (!_appDataFolder.DirectoryExists(ExportsDirectory)) {
                _appDataFolder.CreateDirectory(ExportsDirectory);
            }

            var path = _appDataFolder.Combine(ExportsDirectory, exportFile);
            _appDataFolder.CreateFile(path, exportDocument);

            return _appDataFolder.MapPath(path);
        }

        private void UpdateShell() {
            var descriptor = _shellDescriptorManager.GetShellDescriptor();
            _shellDescriptorManager.UpdateShellDescriptor(descriptor.SerialNumber, descriptor.Features, descriptor.Parameters);
        }
    }
}
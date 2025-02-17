﻿// Copyright (c) 2019 Microsoft Corporation. All Rights Reserved.
// Licensed under the MIT License (MIT)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using RdlMigration.ReportServerApi;
using static RdlMigration.ElementNameConstants;

namespace RdlMigration
{
    /// <summary>
    /// All Server related code are here, including downloading files, getting corresponded
    /// references and dependencies.
    /// </summary>
    public class RdlFileIO
    {
        private static ConcurrentDictionary<string, Tuple<string, string>> dataSourceReferenceNameMap;
        private static IReportingService2010 server;

        static RdlFileIO()
        {
            dataSourceReferenceNameMap = new ConcurrentDictionary<string, Tuple<string, string>>();
        }

        public RdlFileIO(string urlEndpoint)
        {
            dataSourceReferenceNameMap = new ConcurrentDictionary<string, Tuple<string, string>>();
            server = new ReportingService2010
            {
                Url = urlEndpoint + SoapApiConstants.SOAPApiExtension,
                UseDefaultCredentials = true
            };

            
        }

        public RdlFileIO(IReportingService2010 reportserver)
        {
            dataSourceReferenceNameMap = new ConcurrentDictionary<string, Tuple<string, string>>();
            server = reportserver;
        }

        /// <summary>
        /// retrieve the reports in the specified folder.
        /// </summary>
        /// <param name="folderPath">the Path to the folder.</param>
        /// <returns>all the report pathes in the folder. </returns>
        public string[] GetReportsInFolder(string folderPath)
        {
            var catagoryItems = server.ListChildren(folderPath, false).Where(item => item.TypeName == SoapApiConstants.Report);
            var reportPaths = from report in catagoryItems select report.Path;
            return reportPaths.ToArray();
        }
        /// <summary>
        /// Check if the input path is a folder or report. If neither throw an exception.
        /// </summary>
        /// <param name="itemPath">the path to the input iten=m.</param>
        /// <returns>true if it is a folder, false if it is a report. Exception if neither.</returns>
        public bool IsFolder(string itemPath)
        {
            if (server.GetItemType(itemPath) == SoapApiConstants.Report)
            {
                return false;
            }
            else if (server.GetItemType(itemPath) == SoapApiConstants.Folder)
            {
                return true;
            }
            else
            {
                throw new Exception($"{itemPath} is neither a report nor a folder");
            }
        }

        public bool IsReport(string itemPath)
        {
            return (server.GetItemType(itemPath) == SoapApiConstants.Report);
        }

        /// <summary>
        /// Downloads a rdl file from report Server.
        /// </summary>
        /// <param name="filePath"> the file path on the Report server.</param>
        /// <param name="outputPath">The path of downloaded file.</param>
        /// <returns>a reference path to the file.</returns>
        public string DownloadRdl(string filePath, string outputPath)
        {
            string outputFileName = outputPath + Path.GetFileNameWithoutExtension(filePath) + ReportFileExtension;

            var rawContent = server.GetItemDefinition(filePath);

            // Read the whole thing into a file that later we can load and modify into output
            using (Stream file = File.Create(outputFileName))
            {
                file.Write(rawContent, 0, rawContent.Length);
            }

            return outputFileName;
        }

        /// <summary>
        /// Downloads a rdl file from report Server.
        /// </summary>
        /// <param name="filePath"> the file path on the Report server.</param>
        /// <returns>a stream to the file.</returns>
        public Stream DownloadRdl(string filePath)
        {
            var rawContent = server.GetItemDefinition(filePath);
            Stream outputFile = new MemoryStream(rawContent);

            return outputFile;
        }

        /// <summary>
        ///  Gets the dataSet Refernces from a server.
        /// </summary>
        /// <param name="filePath"> The remote path of the report file on server.</param>
        /// <returns>an List of DataSet Reference.</returns>
        public List<KeyValuePair<string, string>> GetDataSetReference(string filePath)
        {
            List<KeyValuePair<string, string>> retList = new List<KeyValuePair<string, string>>();
            foreach (var reference in server.GetItemReferences(filePath, DataSetConstants.DataSet))
            {
                if (reference.Reference == null)
                {
                    throw new Exception($"Bad report: cannot find data set {reference.Name}.");
                }
                retList.Add(new KeyValuePair<string, string>(reference.Name, reference.Reference));
            }

            return retList;
        }

        /// <summary>
        ///     Gets the dataSet from a server.
        /// </summary>
        /// <param name="filePath">The remote path of the report file on server.</param>
        /// <param name="dataSetReferenceMap">Dictionary that map string(reference) to DataSet, for later analysis and naming with datasource.</param>
        /// <returns>an array of XElement represenstation of DataSet Object.</returns>
        public XElement[] GetDataSets(string filePath, out Dictionary<KeyValuePair<string, string>, XElement> dataSetReferenceMap)
        {
            dataSetReferenceMap = new Dictionary<KeyValuePair<string, string>, XElement>();

            var references = GetDataSetReference(filePath);
            var dataSetList = new List<XElement>();
            foreach (var reference in references)
            {
                XElement currDataSet = GetDataSetContent(reference.Value);
                
                var keys = new KeyValuePair<string, string>(filePath, reference.Key);
                if (!dataSetReferenceMap.ContainsKey(keys))
                {
                    // We add the path to the dataset in the service. But also add the map of path+key in case
                    // the Rdl has a different path (common when dataset is set after the fact).
                    dataSetReferenceMap.Add(keys, currDataSet);
                }

                if (currDataSet.Attribute("Name") != null)
                {
                    currDataSet.Attribute("Name").SetValue(reference);
                }
                else
                {
                    currDataSet.Add(new XAttribute("Name", reference));
                }

                dataSetList.Add(currDataSet);
            }

            return dataSetList.ToArray();
        }

        public XElement[] GetDataSets(string filePath)
        {
            return GetDataSets(filePath, out Dictionary<KeyValuePair<string, string>, XElement> retDict);
        }

        /// <summary>
        /// Gets the dataSource Refernces from a server.
        /// </summary>
        /// <param name="filePath">The remote path of the report file on server.</param>
        /// <returns>an List of dataSource Reference.</returns>
        public List<ItemReferenceData> GetDataSourceReference(string filePath)
        {
            var retList = new List<ItemReferenceData>();
            var serverDataSourceReferences = server.GetItemReferences(filePath, DataSourceConstants.DataSource);
            foreach (var reference in serverDataSourceReferences)
            {
                if (reference.Reference == null)
                {
                    throw new Exception($"Bad report: cannot find data source {reference.Name}.");
                }
                retList.Add(reference);
            }

            foreach(var dataSet in server.GetItemReferences(filePath, DataSetConstants.DataSet))
            {
                var dataSetSourceRef = server.GetItemReferences(dataSet.Reference, DataSourceConstants.DataSource).ElementAt(0);
                var retListReferences = retList.Select(x => x.Reference).ToList();

                if (!retListReferences.Contains(dataSetSourceRef.Reference))
                {
                    // rewrite name of datasources referenced from datasets
                    string dataSourceName = SerializeDataSourceName(dataSetSourceRef.Reference, null, filePath);

                    if (!ReportDataSourceExists(filePath, dataSetSourceRef) && !retListReferences.Contains(dataSourceName))
                    {
                        dataSetSourceRef.Name = dataSourceName;
                        retList.Add(dataSetSourceRef);
                    }
                }
            }

            return retList;
        }

        private static bool ReportDataSourceExists(string filePath, ItemReferenceData dataSetSourceRef)
        {
            bool reportDataSourceExists = false;
            if ((server as ReportingService2010) != null)
            {
                var dataSourceConnectionString = server.GetDataSourceContents(dataSetSourceRef.Reference).ConnectString;
                var reportDataSources = (server as ReportingService2010).GetItemDataSources(filePath);
                foreach (var reportDataSource in reportDataSources)
                {
                    string connString;
                    if ((reportDataSource.Item as DataSourceDefinition) != null)
                    {
                        connString = (reportDataSource.Item as DataSourceDefinition).ConnectString;
                    }
                    else
                    {
                        connString = server.GetDataSourceContents((reportDataSource.Item as DataSourceReference).Reference).ConnectString;
                    }

                    if (dataSourceConnectionString == connString)
                    {
                        reportDataSourceExists = true;
                        break;
                    }
                }
            }

            return reportDataSourceExists;
        }


        /// <summary>
        /// Gets the dataSource from a server.
        /// </summary>
        /// <param name="filePath">The remote path of the report file on server.</param>
        /// <returns>an array of DataSource Object.</returns>
        public DataSource[] GetDataSources(string filePath)
        {
            List<ItemReferenceData> references = GetDataSourceReference(filePath);
            List<DataSource> retList = new List<DataSource>();
            foreach (var reference in references)
            {
                DataSource currDataSource = new DataSource
                {
                    Item = server.GetDataSourceContents(reference.Reference),
                    Name = reference.Name
                };

                retList.Add(currDataSource);
            }

            return retList.ToArray();
        }

        public DataSource[] GetUniqueDataSources(string filePath)
        {
            var dataSources = GetDataSources(filePath);
            return dataSources.Select(ds => ds.Name).Distinct().Select(n => dataSources.First(ds => ds.Name.Equals(n))).ToArray();
        }

        /// <summary>
        ///  Take the dataSource objects grabbed from the report server and write them in a file.
        /// </summary>
        /// <param name="dataSources">array of datasources used.</param>
        /// <param name="outputFileName">The path/name of output file.</param>
        public void WriteDataSourceContent(DataSource[] dataSources, string outputFileName)
        {
            XDocument retfile = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement(DataSourceConstants.DataSources));
            string dataSourceContent = string.Empty;
            foreach (var dataSource in dataSources)
            {
                DataSourceDefinition currentDataSource = null;
                if (dataSource.Item.GetType().Equals(new DataSourceReference().GetType()))
                {
                    string dataSourcePath = ((DataSourceReference)dataSource.Item).Reference;
                    currentDataSource = server.GetDataSourceContents(dataSourcePath);

                    byte[] rawDataSourceContent = server.GetItemDefinition(dataSourcePath);
                    dataSourceContent = new UTF8Encoding(true).GetString(rawDataSourceContent);

                    XElement currentDataSourceElement;
                    using (MemoryStream dataSourceMemoryStream = new MemoryStream(rawDataSourceContent))
                    {
                        currentDataSourceElement = XElement.Load(dataSourceMemoryStream);
                    }

                    XAttribute nameAttribute = new XAttribute("Name", dataSource.Name);
                    currentDataSourceElement.Add(nameAttribute);
                    retfile.Root.Add(currentDataSourceElement);
                }
                else
                {
                    currentDataSource = (DataSourceDefinition)dataSource.Item;

                    XAttribute nameAttribute = new XAttribute("Name", dataSource.Name);
                    XElement currentDataSourceElement = new XElement(DataSourceConstants.DataSourceDefinition, nameAttribute);

                    PropertyInfo[] properties = typeof(DataSourceDefinition).GetProperties();
                    foreach (PropertyInfo property in properties)
                    {
                        if (property.GetValue(currentDataSource) != null)
                        {
                            currentDataSourceElement.Add(new XElement(property.Name, property.GetValue(currentDataSource)));
                        }
                    }

                    retfile.Root.Add(currentDataSourceElement);
                }
            }

            retfile.Save(outputFileName);
        }

        /// <summary>
        /// save the dataSets into files in a directory.
        /// </summary>
        /// <param name="dataSets">XElement representation of dataSets.</param>
        /// <param name="outputFolderName">The path/name of output folder.</param>
        public void WriteDataSetContent(XElement[] dataSets, string outputFolderName)
        {
            XNamespace ns2010 = "http://schemas.microsoft.com/sqlserver/reporting/2010/01/shareddatasetdefinition";

            Directory.CreateDirectory(outputFolderName);

            foreach (var dataSetNode in dataSets)
            {
                XNamespace rdNamespace = ReportDesignerNameSpace;
                XDocument retfile = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement(ns2010 + DataSetConstants.SharedDataSet, new XAttribute(XNamespace.Xmlns + "rd", rdNamespace)));

                // if there is a shared dataSet then add the retrieved dataSet, otherwise just add itself
                retfile.Root.Add(dataSetNode);
                var outputFileName = Path.Combine(outputFolderName, SerializeName(DataSetConstants.DataSet, dataSetNode.Attribute("Name").Value)) + DataSetFileExtension;
                retfile.Save(outputFileName);
            }
        }

        /// <summary>
        /// get the dataSet from the server and convert it to an XElement before returning it.
        /// </summary>
        /// <param name="filePath">The path/name of datSet file.</param>
        /// <returns>a proper XElement version of the dataSet.</returns>
        public XElement GetDataSetContent(string filePath)
        {
            var rawDataSetContent = server.GetItemDefinition(filePath);
            XElement dataSetNode;
            using (MemoryStream dataSetMemoryStream = new MemoryStream(rawDataSetContent))
            {
                XElement root = (XElement)XElement.Load(dataSetMemoryStream);
                dataSetNode = root.Element(root.Name.Namespace + "DataSet");
            }

            return dataSetNode;
        }

        /// <summary>
        /// return the name of dataSource with specific reference.
        /// </summary>
        /// <param name="path">teh dataSource reference.</param>
        /// <returns>corresponding dataSource Name.</returns>
        public static string SerializeDataSourceName(string path, string remoteDataSetPath = null, string filePath = null)
        {
            string remoteDataSourceName = path.Split('/').Last();
            string dataSourceNameString = null;

            if (remoteDataSetPath != null && filePath != null)
            {
                var reportDataSource = GetReportDataSourceForRemoteDataSet(remoteDataSetPath, filePath);
                if (reportDataSource != null)
                {
                    dataSourceReferenceNameMap.TryAdd(remoteDataSourceName, new Tuple<string, string>(reportDataSource, null));
                    return reportDataSource;
                } else
                {
                    path = (server as ReportingService2010).GetItemReferences(remoteDataSetPath, DataSourceConstants.DataSource).First().Reference;
                }
            }

            if (!dataSourceReferenceNameMap.TryGetValue(remoteDataSourceName, out Tuple<string, string> dataSourceName))
            {
                if ((server as ReportingService2010) != null)
                {
                    var dataSource = server.GetDataSourceContents(path);
                    var connectionString = dataSource.ConnectString;

                    foreach (var mapDataSource in dataSourceReferenceNameMap)
                    {
                        var mapDataSourceName = mapDataSource.Value.Item2;
                        var mapDataSourceContents = server.GetDataSourceContents(mapDataSourceName);
                        if (connectionString == mapDataSourceContents.ConnectString)
                        {
                            return mapDataSource.Value.Item1;
                        }
                    }
                }

                remoteDataSourceName = new string(remoteDataSourceName.Where(x => char.IsLetterOrDigit(x) || x == '_').ToArray());
                dataSourceNameString = DataSourceConstants.DataSource + '_' + remoteDataSourceName + '_' + Guid.NewGuid().ToString().Replace('-', '_');
                dataSourceReferenceNameMap.TryAdd(remoteDataSourceName, new Tuple<string, string>(dataSourceNameString, path));

                return dataSourceNameString;
            }

            return dataSourceName.Item1;
        }

        public static string GetReportDataSourceForRemoteDataSet(string remoteDataSetPath, string filePath)
        {
            var reportDataSources = (server as ReportingService2010).GetItemDataSources(filePath);
            foreach (var reportDataSource in reportDataSources)
            {
                string connString;
                if ((reportDataSource.Item as DataSourceDefinition) != null)
                {
                    connString = (reportDataSource.Item as DataSourceDefinition).ConnectString;
                }
                else
                {
                    connString = server.GetDataSourceContents((reportDataSource.Item as DataSourceReference).Reference).ConnectString;
                }

                var remoteConnStr = server.GetDataSourceContents(((DataSourceReference)(server as ReportingService2010).GetItemDataSources(remoteDataSetPath)[0].Item).Reference).ConnectString;
                if (remoteConnStr == connString)
                {
                    return reportDataSource.Name;
                }
            }

            return null;
        }

        /// <summary>
        /// combine the reference and baseName to get a unique name.
        /// </summary>
        /// <param name="baseName">the baseName of the object.</param>
        /// <param name="path">object reference/Path.</param>
        /// <returns> a unique name combined of two. </returns>
        public static string SerializeName(string baseName, string path)
        {
            string retName = baseName;
            foreach (string seg in path.Split('/'))
            {
                retName = retName + '_' + seg;
            }

            return retName;
        }
    }
}
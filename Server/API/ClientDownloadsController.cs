﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Remotely.Server.Attributes;
using Remotely.Server.Services;
using Remotely.Shared.Utilities;
using Remotely.Shared.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Permissions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Remotely.Server.API
{
    [Route("api/[controller]")]
    [ApiController]
    public class ClientDownloadsController : ControllerBase
    {
        private readonly IApplicationConfig _appConfig;
        private readonly IDataService _dataService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1);
        private readonly IWebHostEnvironment _hostEnv;
        public ClientDownloadsController(
            IWebHostEnvironment hostEnv,
            IDataService dataService,
            IApplicationConfig appConfig,
            IHttpClientFactory httpClientFactory)
        {
            _hostEnv = hostEnv;
            _appConfig = appConfig;
            _dataService = dataService;
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet("desktop/{platformID}")]
        public async Task<IActionResult> GetDesktop(string platformID)
        {
            return await GetInstallFile(null, platformID);
        }

        [HttpGet("clickonce-setup/{architecture}/{organizationId}")]
        public async Task<IActionResult> GetClickOnceSetup(string architecture, string organizationId)
        {
            string setupFilePath;

            switch (architecture?.ToLower())
            {
                case "x64":
                    setupFilePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Win-x64", "ClickOnce", "setup.exe");
                    break;
                case "x86":
                    setupFilePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Win-x86", "ClickOnce", "setup.exe");
                    break;
                default:
                    return BadRequest();
            }

            using var client = _httpClientFactory.CreateClient();
            var formContent = new MultipartFormDataContent();
            var bytes = await System.IO.File.ReadAllBytesAsync(setupFilePath);
            using var byteContent = new ByteArrayContent(bytes);
            formContent.Add(byteContent, "setup", "setup.exe");

            using var response = await client.PostAsync($"{AppConstants.ClickOnceSetupUrl}/?host={Request.Scheme}://{Request.Host}&organizationid={organizationId}&architecture={architecture}", formContent);
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            return File(responseBytes, "application/octet-stream", "setup.exe");
        }


        [ServiceFilter(typeof(ApiAuthorizationFilter))]
        [HttpGet("{platformID}")]
        public async Task<IActionResult> GetInstaller(string platformID)
        {
            Request.Headers.TryGetValue("OrganizationID", out var orgID);
            return await GetInstallFile(orgID, platformID);
        }

        [HttpGet("{organizationID}/{platformID}")]
        public async Task<IActionResult> GetInstaller(string organizationID, string platformID)
        {
            return await GetInstallFile(organizationID, platformID);
        }

        private async Task<IActionResult> GetBashInstaller(string fileName, string organizationId)
        {
            var scheme = _appConfig.RedirectToHttps ? "https" : Request.Scheme;

            var fileContents = new List<string>();
            fileContents.AddRange(await System.IO.File.ReadAllLinesAsync(Path.Combine(_hostEnv.WebRootPath, "Downloads", fileName)));

            var hostIndex = fileContents.IndexOf("HostName=");
            var orgIndex = fileContents.IndexOf("Organization=");

            fileContents[hostIndex] = $"HostName=\"{scheme}://{Request.Host}\"";
            fileContents[orgIndex] = $"Organization=\"{organizationId}\"";
            var fileBytes = Encoding.UTF8.GetBytes(string.Join("\n", fileContents));
            return File(fileBytes, "application/octet-stream", fileName);
        }

        private async Task<IActionResult> GetDesktopFile(string filePath)
        {
            var relayCode = string.Empty;

            if (User.Identity.IsAuthenticated)
            {
                var currentOrg = await _dataService.GetOrganizationByUserName(User.Identity.Name);
                if (currentOrg.SponsorLevel >= Shared.Enums.SponsorLevel.Relay)
                {
                    relayCode = currentOrg.RelayCode;
                }
            }
            else
            {
                relayCode = await _dataService.GetDefaultRelayCode();
            }

            var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);

            if (!string.IsNullOrWhiteSpace(relayCode))
            {
                var downloadFileName = fileNameWithoutExtension + $"-[{relayCode}]" + Path.GetExtension(filePath);
                return File(fs, "application/octet-stream", downloadFileName);

            }
            else
            {
                return File(fs, "application/octet-stream", fileNameWithoutExtension);
            }
        }

        private async Task<IActionResult> GetInstallFile(string organizationId, string platformID)
        {
            try
            {
                if (await _fileLock.WaitAsync(TimeSpan.FromSeconds(15)))
                {
                    switch (platformID)
                    {
                        case "WindowsDesktop-x64":
                            {
                                var filePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Win-x64", "Remotely_Desktop.exe");
                                return await GetDesktopFile(filePath);
                            }
                        case "WindowsDesktop-x86":
                            {
                                var filePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Win-x86", "Remotely_Desktop.exe");
                                return await GetDesktopFile(filePath);
                            }
                        case "UbuntuDesktop":
                            {
                                var filePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Remotely_Desktop");
                                return await GetDesktopFile(filePath);
                            }
                        case "WindowsInstaller":
                            {
                                var filePath = Path.Combine(_hostEnv.WebRootPath, "Downloads", "Remotely_Installer.exe");
                                var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                var organization = _dataService.GetOrganizationById(organizationId);

                                if (organization.SponsorLevel > Shared.Enums.SponsorLevel.None)
                                {
                                    var relayCode = organization.RelayCode;
                                    return File(fs, "application/octet-stream", $"Remotely_Install-[{relayCode}].exe");

                                }
                                else
                                {
                                    return File(fs, "application/octet-stream", $"Remotely_Installer-[{organizationId}].exe");
                                }
                            }
                        // TODO: Remove after a few releases.
                        case "Manjaro-x64":
                        case "ManjaroInstaller-x64":
                            {
                                var fileName = "Install-Manjaro-x64.sh";

                                return await GetBashInstaller(fileName, organizationId);
                            }
                        // TODO: Remove after a few releases.
                        case "Ubuntu-x64":
                        case "UbuntuInstaller-x64":
                            {
                                var fileName = "Install-Ubuntu-x64.sh";

                                return await GetBashInstaller(fileName, organizationId);
                            }

                        default:
                            return BadRequest();
                    }
                }
                else
                {
                    return StatusCode(StatusCodes.Status408RequestTimeout);
                }
            }
            finally
            {
                if (_fileLock.CurrentCount == 0)
                {
                    _fileLock.Release();
                }
            }
        }
    }
}

Function Set-ResourceGroupExpiry {
    param(
        [Parameter(Position = 1, Mandatory = $True, ValueFromPipelineByPropertyName = $True)]
        $ResourceGroupName,
    
        [string]
        [ValidateSet("1week", "2weeks", "ManualSetup")]
        $ExpiresBy = "1week"
    )

    Process
    {
        Write-Output $ResourceGroupName

        $now = (Get-Date).ToUniversalTime()
        if ($ExpiresBy -eq "1week") {
            $rgExpiresBy = $now.AddDays(7).GetDateTimeFormats("o")
        }
        elseif ($ExpiresBy -eq "2weeks")
        {
            $rgExpiresBy = $now.AddDays(14).GetDateTimeFormats("o")
        }
        else{
            $rgExpiresBy = $ExpiresBy
        }

        $tags = (Get-AzureRmResourceGroup -Name $ResourceGroupName).Tags
        $tags["CreatedBy"] = $env:USERNAME
        $tags["ExpiresBy"] = $rgExpiresBy
        Set-AzureRmResourceGroup -Name $ResourceGroupName -Tag $tags
    }
}

#requires -Version 5

function Select-DesktopPetSuccessfulBuildWorkflowRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$WorkflowRuns,
        [Parameter(Mandatory = $true)][string]$ExpectedRepository,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedHeadSha,
        [Parameter(Mandatory = $true)][string]$ExpectedHeadBranch,
        [string]$ExpectedWorkflowPath = '.github/workflows/build.yml',
        [string]$ExpectedEvent = 'push'
    )

    if ($ExpectedRepository -cnotmatch
        '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Expected repository is invalid: '$ExpectedRepository'."
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedHeadBranch) -or
        [string]::IsNullOrWhiteSpace($ExpectedWorkflowPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedEvent)) {
        throw 'Expected workflow identity fields cannot be empty.'
    }

    $matchingRuns = @(
        $WorkflowRuns |
            Where-Object {
                [long]$_.id -gt 0 -and
                [string]$_.head_sha -ceq $ExpectedHeadSha -and
                [string]$_.head_branch -ceq $ExpectedHeadBranch -and
                [string]$_.path -ceq $ExpectedWorkflowPath -and
                [string]$_.event -ceq $ExpectedEvent -and
                [string]$_.status -ceq 'completed' -and
                [string]$_.conclusion -ceq 'success' -and
                [string]$_.repository.full_name -ceq
                    $ExpectedRepository -and
                [string]$_.head_repository.full_name -ceq
                    $ExpectedRepository
            }
    )
    if ($matchingRuns.Count -eq 0) {
        $observed = @(
            $WorkflowRuns |
                ForEach-Object {
                    '{0}:{1}:{2}:{3}:{4}:{5}' -f
                        [string]$_.path,
                        [string]$_.event,
                        [string]$_.head_branch,
                        [string]$_.head_sha,
                        [string]$_.status,
                        [string]$_.conclusion
                } |
                Sort-Object -Unique
        )
        if ($observed.Count -eq 0) {
            $observed = @('none')
        }
        throw (
            "No successful '$ExpectedWorkflowPath' $ExpectedEvent run from " +
            "'$ExpectedRepository' exists for $ExpectedHeadBranch@" +
            "${ExpectedHeadSha}. Observed: $($observed -join '; ')")
    }

    return @(
        $matchingRuns |
            Sort-Object `
                @{ Expression = { [long]$_.run_attempt }; Descending = $true },
                @{ Expression = { [long]$_.run_number }; Descending = $true },
                @{ Expression = { [long]$_.id }; Descending = $true } |
            Select-Object -First 1
    )[0]
}

function Assert-DesktopPetRequiredBuildWorkflowJobs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Jobs,
        [Parameter(Mandatory = $true)]
        [ValidateRange(1, [long]::MaxValue)][long]$ExpectedRunId,
        [string[]]$RequiredJobNames = @(
            'Validate fortune labeling pipeline',
            'Build, test, audit, and package unsigned x64'
        )
    )

    if ($RequiredJobNames.Count -eq 0 -or
        @($RequiredJobNames | Sort-Object -Unique).Count -ne
            $RequiredJobNames.Count) {
        throw 'Required build workflow job names must be non-empty and unique.'
    }

    foreach ($requiredJob in $RequiredJobNames) {
        if ([string]::IsNullOrWhiteSpace($requiredJob)) {
            throw 'Required build workflow job names cannot be empty.'
        }
        $matchingJobs = @(
            $Jobs |
                Where-Object {
                    [long]$_.run_id -eq $ExpectedRunId -and
                    [string]$_.name -ceq $requiredJob
                }
        )
        if ($matchingJobs.Count -ne 1) {
            throw (
                "Expected exactly one '$requiredJob' job in authenticated " +
                "build workflow run $ExpectedRunId; found " +
                "$($matchingJobs.Count).")
        }
        $job = $matchingJobs[0]
        if ([string]$job.status -cne 'completed' -or
            [string]$job.conclusion -cne 'success') {
            throw (
                "Authenticated build workflow job '$requiredJob' did not " +
                "succeed in run ${ExpectedRunId}: " +
                "$([string]$job.status)/$([string]$job.conclusion)")
        }
    }
}

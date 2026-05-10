# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

Write-Comment -prefix "." -text "Publishing the InterleaveX documentation to GitHub" -color "yellow"
& mkdocs gh-deploy
Write-Comment -prefix "." -text "Done" -color "green"

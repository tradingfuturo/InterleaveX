# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

Write-Comment -prefix "." -text "Publishing the InterleaveX documentation to GitHub" -color "yellow"
& mkdocs gh-deploy
Write-Comment -prefix "." -text "Done" -color "green"

#!/usr/bin/perl
# Copyright 2019 Google LLC
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#   https://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

use FindBin;
use File::Find qw(find);
use strict;
use warnings;

chdir "$FindBin::Bin/../.." or die "Failed to locate repository root: $!";
my $defs_file = "third_party/monaco-editor-0.14.3/min/vs/basic-languages/javascript/javascript.js";
my $js_keywords = '"break","case","catch","class","continue","const","constructor","debugger","default","delete","do","else","export","extends","false","finally","for","from","function","get","if","import","in","instanceof","let","new","null","return","set","super","switch","symbol","this","throw","true","try","typeof","undefined","var","void","while","with","yield","async","await","of"';

open my $defs_handle, '<:raw', $defs_file or die "Failed to read Monaco js defs file from $defs_file: $!";
local $/;
my $jsdef = <$defs_handle>;
close $defs_handle or die "Failed to close $defs_file: $!";

my @words = $js_keywords =~ /"(\w+)"/g;
my @js_files;
find(
  {
    no_chdir => 1,
    wanted => sub {
      return unless -f $_ && /\.js\.txt\z/;
      push @js_files, $File::Find::name;
    },
  },
  "Assets/Scripts/Behaviors/JavaScript/apiv2"
);

for my $js_file (@js_files) {
  open my $js_handle, '<:raw', $js_file or die "Failed to read JS file $js_file: $!";
  my $contents = <$js_handle>;
  close $js_handle or die "Failed to close $js_file: $!";
  next unless $contents =~ /VISIBLE_TO_MONACO/;
  print STDERR "Processing JS file $js_file...\n";

  $contents =~ s/^\/\/\s+DOC_ONLY:\s+//mg;

  my @functions = ($contents =~ /^function (\w+)\(/mg);
  print "FUNCTION: $_\n" for @functions;
  push @words, @functions;

  my @globs = ($contents =~ /^var (\w+)/mg);
  print "GLOB: $_\n" for @globs;
  push @words, @globs;
}

@words = sort @words;

my @words_quoted = map { qq{"$_"} } @words;
my $list = join(",", @words_quoted);
$jsdef =~ s/tokenPostfix:\s*"\.js"\s*,\s*keywords:\s*\[[^]]+\]/tokenPostfix:".js",keywords:[$list]/
  or die "Failed to match keywords: portion of Monaco js defs.";

open my $out, '>:raw', $defs_file or die "Failed to write $defs_file: $!";
print {$out} $jsdef;
close $out or die "Failed to close $defs_file: $!";
print STDERR "Done.\n";

